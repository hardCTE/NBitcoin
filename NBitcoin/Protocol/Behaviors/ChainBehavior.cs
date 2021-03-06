﻿#if !NOSOCKET
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NBitcoin.Protocol.Behaviors
{

	/// <summary>
	/// The Chain Behavior is responsible for keeping a ConcurrentChain up to date with the peer, it also responds to getheaders messages.
	/// </summary>
	public class ChainBehavior : NodeBehavior
	{
		public ChainBehavior(ConcurrentChain chain)
		{
			if(chain == null)
				throw new ArgumentNullException("chain");
			_Chain = chain;
			AutoSync = true;
			CanSync = true;
			CanRespondToGetHeaders = true;
		}
		/// <summary>
		/// Keep the chain in Sync (Default : true)
		/// </summary>
		public bool CanSync
		{
			get;
			set;
		}
		/// <summary>
		/// Respond to getheaders messages (Default : true)
		/// </summary>
		public bool CanRespondToGetHeaders
		{
			get;
			set;
		}

		ConcurrentChain _Chain;
		public ConcurrentChain Chain
		{
			get
			{
				return _Chain;
			}
			set
			{
				AssertNotAttached();
				_Chain = value;
			}
		}

		int _SynchingCount;
		/// <summary>
		/// Using for test, this might not be reliable
		/// </summary>
		internal bool Synching
		{
			get
			{
				return _SynchingCount != 0;
			}
		}

		Timer _Refresh;
		protected override void AttachCore()
		{
			_Refresh = new Timer(o =>
			{
				if(AutoSync)
					TrySync();
			}, null, 0, (int)TimeSpan.FromMinutes(10).TotalMilliseconds);
			RegisterDisposable(_Refresh);
			if(AttachedNode.State == NodeState.Connected)
			{
				AttachedNode.MyVersion.StartHeight = Chain.Height;
			}
			AttachedNode.StateChanged += AttachedNode_StateChanged;
			RegisterDisposable(AttachedNode.Filters.Add(Intercept));
		}

		void Intercept(IncomingMessage message, Action act)
		{
			var inv = message.Message.Payload as InvPayload;
			if(inv != null)
			{
				if(inv.Inventory.Any(i => ((i.Type & InventoryType.MSG_BLOCK) != 0) && !Chain.Contains(i.Hash)))
				{
					_Refresh.Dispose(); //No need of periodical refresh, the peer is notifying us
					if(AutoSync)
						TrySync();
				}
			}

			var getheaders = message.Message.Payload as GetHeadersPayload;
			if(getheaders != null && CanRespondToGetHeaders)
			{
				HeadersPayload headers = new HeadersPayload();
				var fork = Chain.FindFork(getheaders.BlockLocators);
				if(fork != null)
					foreach(var header in Chain.EnumerateToTip(fork).Skip(1))
					{
						headers.Headers.Add(header.Header);
						if(header.HashBlock == getheaders.HashStop || headers.Headers.Count == 2000)
							break;
					}
				AttachedNode.SendMessageAsync(headers);
			}

			var newheaders = message.Message.Payload as HeadersPayload;
			var pendingTipBefore = GetPendingTip();
			if(newheaders != null && CanSync)
			{
				var tip = GetPendingTip();
				foreach(var header in newheaders.Headers)
				{
					var prev = tip.FindAncestorOrSelf(header.HashPrevBlock);
					if(prev == null)
						break;
					tip = new ChainedBlock(header, header.GetHash(), prev);
					var validated = Chain.GetBlock(tip.HashBlock) != null || tip.Validate(AttachedNode.Network);
					validated &= !IsMarkedInvalid(tip.HashBlock);
					if(!validated)
					{
						invalidHeaderReceived = true;
						break;
					}
					_PendingTip = tip;
				}
				if(_PendingTip.ChainWork > Chain.Tip.ChainWork)
				{
					Chain.SetTip(_PendingTip);
				}

				var chainedPendingTip = Chain.GetBlock(_PendingTip.HashBlock);
				if(chainedPendingTip != null)
				{
					_PendingTip = chainedPendingTip; //This allows garbage collection to collect the duplicated pendingtip and ancestors
				}
				if(newheaders.Headers.Count != 0 && pendingTipBefore.HashBlock != GetPendingTip().HashBlock)
					TrySync();
				Interlocked.Decrement(ref _SynchingCount);
			}

			act();
		}

		private bool IsMarkedInvalid(uint256 hashBlock)
		{
			try
			{
				_InvalidBlocksLock.EnterReadLock();
				return _InvalidBlocks.Contains(hashBlock);
			}
			finally
			{
				_InvalidBlocksLock.ExitReadLock();
			}
		}

		public void MarkBlockInvalid(uint256 blockHash)
		{
			try
			{
				_InvalidBlocksLock.EnterWriteLock();
				_InvalidBlocks.Add(blockHash);
			}
			finally
			{
				_InvalidBlocksLock.ExitWriteLock();
			}
		}

		/// <summary>
		/// Check if any past blocks announced by this peer is in the invalid blocks list, and set InvalidHeaderReceived flag accordingly
		/// </summary>
		/// <returns>True if no invalid block is received</returns>
		public bool CheckAnnouncedBlocks()
		{
			var tip = _PendingTip;
			if(tip != null && !invalidHeaderReceived)
			{
				try
				{
					_InvalidBlocksLock.EnterReadLock();
					if(_InvalidBlocks.Count != 0)
					{
						foreach(var header in tip.EnumerateToGenesis())
						{
							if(invalidHeaderReceived)
								break;
							invalidHeaderReceived |= _InvalidBlocks.Contains(header.HashBlock);
						}
					}
				}
				finally
				{
					_InvalidBlocksLock.ExitReadLock();
				}
			}
			return !invalidHeaderReceived;
		}

		/// <summary>
		/// Sync the chain as headers come from the network (Default : true)
		/// </summary>
		public bool AutoSync
		{
			get;
			set;
		}

		ChainedBlock _PendingTip; //Might be different than Chain.Tip, in the rare event of large fork > 2000 blocks

		private bool invalidHeaderReceived;
		public bool InvalidHeaderReceived
		{
			get
			{
				return invalidHeaderReceived;
			}
		}

		void AttachedNode_StateChanged(Node node, NodeState oldState)
		{
			TrySync();
		}

		/// <summary>
		/// Asynchronously try to sync the chain
		/// </summary>
		public void TrySync()
		{
			var node = AttachedNode;
			if(node != null)
			{
				if(node.State == NodeState.HandShaked && CanSync && !invalidHeaderReceived)
				{
					Interlocked.Increment(ref _SynchingCount);
					node.SendMessageAsync(new GetHeadersPayload()
					{
						BlockLocators = GetPendingTip().GetLocator()
					});
				}
			}
		}

		private ChainedBlock GetPendingTip()
		{
			_PendingTip = _PendingTip ?? Chain.Tip;
			return _PendingTip;
		}

		protected override void DetachCore()
		{
			AttachedNode.StateChanged -= AttachedNode_StateChanged;
		}


		ReaderWriterLockSlim _InvalidBlocksLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
		HashSet<uint256> _InvalidBlocks = new HashSet<uint256>();
		#region ICloneable Members

		public override object Clone()
		{
			var clone = new ChainBehavior(Chain)
			{
				CanSync = CanSync,
				CanRespondToGetHeaders = CanRespondToGetHeaders,
				AutoSync = AutoSync,
				_InvalidBlocks = _InvalidBlocks,
				_InvalidBlocksLock = _InvalidBlocksLock
			};
			return clone;
		}

		#endregion
	}
}
#endif