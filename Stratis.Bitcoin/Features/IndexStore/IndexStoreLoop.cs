﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Base;
using Stratis.Bitcoin.BlockPulling;
//using Stratis.Bitcoin.Common.Hosting;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.Features.IndexStore
{
    public class IndexStoreLoop
    {
        private readonly ConcurrentChain chain;
        public IndexRepository IndexRepository { get; } // public for testing
        private readonly NodeSettings nodeArgs;
        private readonly StoreBlockPuller blockPuller;
        private readonly IndexStoreCache indexStoreCache;
        private readonly INodeLifetime nodeLifetime;
        private readonly IAsyncLoopFactory asyncLoopFactory;
        private readonly IndexStoreStats indexStoreStats;
        private readonly ILogger storeLogger;

        public ChainState ChainState { get; }
        public ConcurrentDictionary<uint256, BlockPair> PendingStorage { get; }
        public ChainedBlock StoredBlock { get; private set; }

        public IndexStoreLoop(ConcurrentChain chain,
            IndexRepository indexRepository,
            NodeSettings nodeArgs,
            ChainState chainState,
            StoreBlockPuller blockPuller,
            IndexStoreCache cache,
            INodeLifetime nodeLifetime,
            IAsyncLoopFactory asyncLoopFactory,
            ILoggerFactory loggerFactory)
        {
            this.chain = chain;
            this.IndexRepository = indexRepository;
            this.nodeArgs = nodeArgs;
            this.blockPuller = blockPuller;
            this.ChainState = chainState;
            this.indexStoreCache = cache;
            this.nodeLifetime = nodeLifetime;
            this.asyncLoopFactory = asyncLoopFactory;
            this.storeLogger = loggerFactory.CreateLogger(this.GetType().FullName);

            this.PendingStorage = new ConcurrentDictionary<uint256, BlockPair>();
            this.indexStoreStats = new IndexStoreStats(this.IndexRepository, this.indexStoreCache, this.storeLogger);
        }

        public class BlockPair
        {
            public Block Block;
            public ChainedBlock ChainedBlock;
        }

        // downaloading 5mb is not much in case the store need to catchup
        private uint insertsizebyte = 1000000 * 5; // Block.MAX_BLOCK_SIZE 
        private int batchtriggersize = 5;
        private int batchdownloadsize = 1000;
        private TimeSpan pushInterval = TimeSpan.FromSeconds(10);
        private readonly TimeSpan pushIntervalIBD = TimeSpan.FromMilliseconds(100);

        public async Task Initialize()
        {
            if (this.nodeArgs.Store.ReIndex)
                throw new NotImplementedException();

            this.StoredBlock = this.chain.GetBlock(this.IndexRepository.BlockHash);
            if (this.StoredBlock == null)
            {
                // the store is out of sync, this can happen if the node crashed 
                // or was not closed down properly and bestchain tip is not 
                // the same as in store tip, to recover we walk back the chain til  
                // a common block header is found and set the block store tip to that

                var blockstoreResetList = new List<uint256>();
                var resetBlock = await this.IndexRepository.GetAsync(this.IndexRepository.BlockHash);
                var resetBlockHash = resetBlock.GetHash();
                // walk back the chain and find the common block
                while (this.chain.GetBlock(resetBlockHash) == null)
                {
                    blockstoreResetList.Add(resetBlockHash);
                    if (resetBlock.Header.HashPrevBlock == this.chain.Genesis.HashBlock)
                    {
                        resetBlockHash = this.chain.Genesis.HashBlock;
                        break;
                    }
                    resetBlock = await this.IndexRepository.GetAsync(resetBlock.Header.HashPrevBlock);
                    Guard.NotNull(resetBlock, nameof(resetBlock));
                    resetBlockHash = resetBlock.GetHash();
                }

                var newTip = this.chain.GetBlock(resetBlockHash);
                await this.IndexRepository.DeleteAsync(newTip.HashBlock, blockstoreResetList);
                this.StoredBlock = newTip;
                this.storeLogger.LogWarning($"IndexStore Initialize recovering to block height = {newTip.Height} hash = {newTip.HashBlock}");
            }
            /*
            if (this.nodeArgs.Store.TxIndex != this.BlockRepository.TxIndex)
            {
                if (this.StoredBlock != this.chain.Genesis)
                    throw new IndexStoreException("You need to rebuild the database using -reindex-chainstate to change -txindex");
                if (this.nodeArgs.Store.TxIndex)
                    await this.BlockRepository.SetTxIndex(this.nodeArgs.Store.TxIndex);
            }
            */
            await this.IndexRepository.SetTxIndex(true);

            this.ChainState.HighestIndexedBlock = this.StoredBlock;
            this.StartLoop();
        }

        public void AddToPending(Block block)
        {
            var chainedBlock = this.chain.GetBlock(block.GetHash());
            if (chainedBlock == null)
                return; // reorg

            // check the size of pending in memory

            // add to pending blocks
            if (this.StoredBlock.Height < chainedBlock.Height)
                this.PendingStorage.TryAdd(chainedBlock.HashBlock, new BlockPair { Block = block, ChainedBlock = chainedBlock });
        }

        public Task Flush()
        {
            return this.DownloadAndStoreBlocks(CancellationToken.None, true);
        }


        public void StartLoop()
        {
            // A loop that writes pending blocks to store 
            // or downloads missing blocks then writing to store
            this.asyncLoopFactory.Run("IndexStoreLoop.DownloadBlocks", async token =>
            {
                await this.DownloadAndStoreBlocks(this.nodeLifetime.ApplicationStopping);
            },
            this.nodeLifetime.ApplicationStopping,
            repeatEvery: TimeSpans.Second,
            startAfter: TimeSpans.FiveSeconds);
        }

        public async Task DownloadAndStoreBlocks(CancellationToken token, bool disposemode = false)
        {
            // TODO: add support to BlockStoreLoop to unset LazyLoadingOn when not in IBD
            // When in IBD we may need many reads for the block key without fetching the block
            // So the repo starts with LazyLoadingOn = true, however when not anymore in IBD 
            // a read is normally done when a peer is asking for the entire block (not just the key) 
            // then if LazyLoadingOn = false the read will be faster on the entire block            

            while (!token.IsCancellationRequested)
            {
                if (this.StoredBlock.Height >= this.ChainState.HighestValidatedPoW?.Height)
                    break;

                // find next block to download
                var next = this.chain.GetBlock(this.StoredBlock.Height + 1);
                if (next == null)
                    break; //no blocks to store

                if (this.indexStoreStats.CanLog)
                {
                    this.indexStoreStats.Log();
                }

                // reorg logic
                if (this.StoredBlock.HashBlock != next.Header.HashPrevBlock)
                {
                    if (disposemode)
                        break;

                    var blockstoremove = new List<uint256>();
                    var remove = this.StoredBlock;
                    // reorg - we need to delete blocks, start walking back the chain
                    while (this.chain.GetBlock(remove.HashBlock) == null)
                    {
                        blockstoremove.Add(remove.HashBlock);
                        remove = remove.Previous;
                    }

                    await this.IndexRepository.DeleteAsync(remove.HashBlock, blockstoremove);
                    this.StoredBlock = remove;
                    this.ChainState.HighestIndexedBlock = this.StoredBlock;
                    break;
                }

                if (await this.IndexRepository.ExistAsync(next.HashBlock))
                {
                    // next block is in storage update StoredBlock 
                    await this.IndexRepository.SetBlockHash(next.HashBlock);
                    this.StoredBlock = next;
                    this.ChainState.HighestIndexedBlock = this.StoredBlock;
                    continue;
                }

                // check if the next block is in pending storage
                // then loop over the pending items and push to store in batches
                // if a stop condition is met break from the loop back to the start
                BlockPair insert;
                if (this.PendingStorage.TryGetValue(next.HashBlock, out insert))
                {
                    // if in IBD and batch is not full then wait for more blocks
                    if (this.ChainState.IsInitialBlockDownload && !disposemode)
                        if (this.PendingStorage.Skip(0).Count() < this.batchtriggersize) // ConcurrentDictionary perf
                            break;

                    if (!this.PendingStorage.TryRemove(next.HashBlock, out insert))
                        break;

                    var tostore = new List<BlockPair>(new[] { insert });
                    var storebest = next;
                    var insertSize = insert.Block.GetSerializedSize();
                    while (!token.IsCancellationRequested)
                    {
                        var old = next;
                        next = this.chain.GetBlock(next.Height + 1);

                        var stop = false;
                        // stop if at the tip or block is already in store or pending insertion
                        if (next == null) stop = true;
                        else if (next.Header.HashPrevBlock != old.HashBlock) stop = true;
                        else if (next.Height > this.ChainState.HighestValidatedPoW?.Height) stop = true;
                        else if (!this.PendingStorage.TryRemove(next.HashBlock, out insert)) stop = true;

                        if (stop)
                        {
                            if (!tostore.Any())
                                break;
                        }
                        else
                        {
                            tostore.Add(insert);
                            storebest = next;
                            insertSize += insert.Block.GetSerializedSize(); // TODO: add the size to the result coming from the signaler	
                        }

                        if (insertSize > this.insertsizebyte || stop)
                        {
                            // store missing blocks and remove them from pending blocks
                            await this.IndexRepository.PutAsync(storebest.HashBlock, tostore.Select(b => b.Block).ToList());
                            this.StoredBlock = storebest;
                            this.ChainState.HighestIndexedBlock = this.StoredBlock;

                            if (stop) break;

                            tostore.Clear();
                            insertSize = 0;

                            // this can be twicked if insert is effecting the consensus speed
                            if (this.ChainState.IsInitialBlockDownload)
                                await Task.Delay(this.pushIntervalIBD, token);
                        }
                    }

                    continue;
                }

                if (disposemode)
                    break;

                // continuously download blocks until a stop condition is found.
                // there are two operations, one is finding blocks to download 
                // and asking them to the puller and the other is collecting
                // downloaded blocks and persisting them as a batch.
                var store = new List<BlockPair>();
                var downloadStack = new Queue<ChainedBlock>(new[] { next });
                this.blockPuller.AskBlock(next);

                int insertDownloadSize = 0;
                int stallCount = 0;
                bool download = true;
                while (!token.IsCancellationRequested)
                {
                    if (download)
                    {
                        var old = next;
                        next = this.chain.GetBlock(old.Height + 1);

                        var stop = false;
                        // stop if at the tip or block is already in store or pending insertion
                        if (next == null) stop = true;
                        else if (next.Header.HashPrevBlock != old.HashBlock) stop = true;
                        else if (next.Height > this.ChainState.HighestValidatedPoW?.Height) stop = true;
                        else if (this.PendingStorage.ContainsKey(next.HashBlock)) stop = true;
                        else if (await this.IndexRepository.ExistAsync(next.HashBlock)) stop = true;

                        if (stop)
                        {
                            if (!downloadStack.Any())
                                break;

                            download = false;
                        }
                        else
                        {
                            this.blockPuller.AskBlock(next);
                            downloadStack.Enqueue(next);

                            if (downloadStack.Count == this.batchdownloadsize)
                                download = false;
                        }
                    }

                    BlockPuller.DownloadedBlock block;
                    if (this.blockPuller.TryGetBlock(downloadStack.Peek(), out block))
                    {
                        var downloadbest = downloadStack.Dequeue();
                        store.Add(new BlockPair { Block = block.Block, ChainedBlock = downloadbest });
                        insertDownloadSize += block.Length;
                        stallCount = 0;

                        // can we push
                        if (insertDownloadSize > this.insertsizebyte || !downloadStack.Any()) // this might go above the max insert size
                        {
                            await this.IndexRepository.PutAsync(downloadbest.HashBlock, store.Select(t => t.Block).ToList());
                            this.StoredBlock = downloadbest;
                            this.ChainState.HighestIndexedBlock = this.StoredBlock;
                            insertDownloadSize = 0;
                            store.Clear();

                            if (!downloadStack.Any())
                                break;
                        }
                    }
                    else
                    {
                        // if a block is stalled or lost to the downloader 
                        // this will make sure the loop start again after a threshold
                        if (stallCount > 10000)
                            break;

                        // waiting for blocks so sleep 100 ms
                        await Task.Delay(100, token);
                        stallCount++;
                    }
                }
            }
        }
    }
}
