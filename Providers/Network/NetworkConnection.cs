﻿//
// NetworkConnection.cs
//
// Author:
//   Eric Maupin <me@ermau.com>
//
// Copyright (c) 2011 Eric Maupin
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Tempest.InternalProtocol;

#if NET_4
using System.Collections.Concurrent;
using System.Threading;
#endif

namespace Tempest.Providers.Network
{
	public abstract class NetworkConnection
		: IConnection
	{
		protected NetworkConnection (IEnumerable<Protocol> protocols)
		{
			if (protocols == null)
				throw new ArgumentNullException ("protocols");

			this.protocols = new Dictionary<byte, Protocol>();
			foreach (Protocol p in protocols)
			{
				if (p == null)
					throw new ArgumentNullException ("protocols", "protocols contains a null protocol");
				if (this.protocols.ContainsKey (p.id))
					throw new ArgumentException ("Only one version of a protocol may be specified");

				this.protocols.Add (p.id, p);
			}

			this.protocols[1] = TempestMessage.InternalProtocol;
		}

		/// <summary>
		/// Raised when a message is received.
		/// </summary>
		public event EventHandler<MessageEventArgs> MessageReceived;

		/// <summary>
		/// Raised when a message has completed sending.
		/// </summary>
		public event EventHandler<MessageEventArgs> MessageSent;

		/// <summary>
		/// Raised when the connection is lost or manually disconnected.
		/// </summary>
		public event EventHandler<DisconnectedEventArgs> Disconnected;

		public bool IsConnected
		{
			get
			{
				lock (this.stateSync)
					return (!this.disconnecting && this.reliableSocket != null && this.reliableSocket.Connected);
			}
		}

		public MessagingModes Modes
		{
			get { return MessagingModes.Async; }
		}

		public EndPoint RemoteEndPoint
		{
			get;
			protected set;
		}

		public IEnumerable<MessageEventArgs> Tick()
		{
			throw new NotSupportedException();
		}

		public virtual void Send (Message message)
		{
			if (message == null)
				throw new ArgumentNullException ("message");
			if (!IsConnected)
				return;

			SocketAsyncEventArgs eargs = null;
			#if NET_4
			if (!writerAsyncArgs.TryPop (out eargs))
			{
				while (eargs == null)
				{
					int count = bufferCount;
					if (count == BufferLimit)
					{
						SpinWait wait = new SpinWait();
						while (!writerAsyncArgs.TryPop (out eargs))
							wait.SpinOnce();

						eargs.AcceptSocket = null;
					}
					else if (count == Interlocked.CompareExchange (ref bufferCount, count + 1, count))
					{
						eargs = new SocketAsyncEventArgs();
						eargs.Completed += ReliableSendCompleted;
						eargs.SetBuffer (new byte[1024], 0, 1024);
					}
				}
			}
			else
				eargs.AcceptSocket = null;
			#else
			while (eargs == null)
			{
				lock (writerAsyncArgs)
				{
					if (writerAsyncArgs.Count != 0)
					{
						eargs = writerAsyncArgs.Pop();
						eargs.AcceptSocket = null;
					}
					else
					{
						if (bufferCount != BufferLimit)
						{
							bufferCount++;
							eargs = new SocketAsyncEventArgs();
							eargs.Completed += ReliableSendCompleted;
							eargs.SetBuffer (new byte[1024], 0, 1024);
						}
					}
				}
			}
			#endif

			int length;
			byte[] buffer = GetBytes (message, out length, eargs.Buffer);

			eargs.SetBuffer (buffer, 0, length);
			eargs.UserToken = message;

			bool sent;
			lock (this.stateSync)
			{
				if (!IsConnected)
				{
					#if !NET_4
					lock (writerAsyncArgs)
					#endif
					writerAsyncArgs.Push (eargs);

					return;
				}

				int p = Interlocked.Increment (ref this.pendingAsync);
				Trace.WriteLine (String.Format ("Increment pending: {0}", p), String.Format ("{1} Send({0})", message, GetType().Name));
				sent = !this.reliableSocket.SendAsync (eargs);
			}

			if (sent)
				ReliableSendCompleted (this.reliableSocket, eargs);
		}

		public void Disconnect (bool now, DisconnectedReason reason = DisconnectedReason.Unknown)
		{
			Trace.WriteLine ("Entering", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

			lock (this.stateSync)
			{
				Trace.WriteLine ("Got state lock.", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

				if (this.disconnecting || this.reliableSocket == null)
				{
					Trace.WriteLine ("Already disconnected, exiting", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));
					return;
				}

				if (!this.reliableSocket.Connected)
				{
					Trace.WriteLine ("Socket not connected, finishing cleanup.", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

					Recycle();
					this.reliableSocket = null;

					while (this.pendingAsync > ((connecting) ? 1 : 0))
						Thread.Sleep (0);

					OnDisconnected (new DisconnectedEventArgs (this, reason));
				}
				else if (now)
				{
					Trace.WriteLine ("Shutting down socket.", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

					this.reliableSocket.Shutdown (SocketShutdown.Both);
					this.reliableSocket.Disconnect (true);
					Recycle();
					this.reliableSocket = null;

					Trace.WriteLine (String.Format ("Waiting for pending ({1}) async (connecting: {0}).", connecting, pendingAsync), String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

					while (this.pendingAsync > ((connecting) ? 1 : 0))
						Thread.Sleep (0);

					Trace.WriteLine ("Finished waiting, raising Disconnected.", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

					OnDisconnected (new DisconnectedEventArgs (this, reason));
				}
				else
				{
					Trace.WriteLine ("Disconnecting asynchronously.", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

					this.disconnecting = true;
					this.disconnectingReason = reason;

					int p = Interlocked.Increment (ref this.pendingAsync);
					Trace.WriteLine (String.Format ("Increment pending: {0}", p), String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

					ThreadPool.QueueUserWorkItem (s =>
					{
						Trace.WriteLine (String.Format ("Async DC waiting for pending ({0}) async.", pendingAsync), String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

						while (this.pendingAsync > 1) // Disconnect is pending.
							Thread.Sleep (0);

						Trace.WriteLine ("Finished waiting, disconnecting async.", String.Format ("{2} Disconnect({0},{1})",now,reason, GetType().Name));

						var args = new SocketAsyncEventArgs { DisconnectReuseSocket = true };
						args.Completed += OnDisconnectCompleted;
						if (!this.reliableSocket.DisconnectAsync (args))
							OnDisconnectCompleted (this.reliableSocket, args);
					});
				}
			}
		}

		public void Dispose()
		{
			Dispose (true);
		}

		protected bool disposed;

		private const int BaseHeaderLength = 7;
		private int maxMessageLength = 1048576;

		protected Dictionary<byte, Protocol> protocols;

		protected readonly object stateSync = new object();
		protected int pendingAsync = 0;
		protected bool disconnecting = false;
		protected bool connecting = false;
		protected DisconnectedReason disconnectingReason;

		protected Socket reliableSocket;

		protected byte[] rmessageBuffer = new byte[20480];
		protected BufferValueReader rreader;
		private int rmessageOffset = 0;
		private int rmessageLoaded = 0;

		protected void Dispose (bool disposing)
		{
			if (this.disposed)
				return;

			Disconnect (true);

			this.disposed = true;
		}

		protected virtual void Recycle()
		{
		}

		protected virtual void OnMessageReceived (MessageEventArgs e)
		{
			var mr = this.MessageReceived;
			if (mr != null)
				mr (this, e);
		}

		protected virtual void OnDisconnected (DisconnectedEventArgs e)
		{
			var dc = this.Disconnected;
			if (dc != null)
				dc (this, e);
		}

		protected virtual void OnMessageSent (MessageEventArgs e)
		{
			var sent = this.MessageSent;
			if (sent != null)
				sent (this, e);
		}

		protected byte[] GetBytes (Message message, out int length, byte[] buffer)
		{
			var writer = new BufferValueWriter (buffer);
			writer.WriteByte (message.Protocol.id);
			writer.WriteUInt16 (message.MessageType);
			writer.Length += sizeof (int); // length placeholder

			message.WritePayload (writer);

			Buffer.BlockCopy (BitConverter.GetBytes (writer.Length), 0, writer.Buffer, BaseHeaderLength - sizeof(int), sizeof(int));

			length = writer.Length;

			return writer.Buffer;
		}

		protected MessageHeader GetHeader (byte[] buffer, int offset)
		{
			ushort type;
			int mlen;
			try
			{
				type = BitConverter.ToUInt16 (buffer, offset + 1);
				mlen = BitConverter.ToInt32 (buffer, offset + 1 + sizeof (ushort));
			}
			catch
			{
				return null;
			}

			Protocol p;
			if (!this.protocols.TryGetValue (buffer[offset], out p))
				return null;

			Message msg = p.Create (type);
			if (msg == null)
				return null;

			return new MessageHeader (p, msg, mlen);
		}

		private void BufferMessages (ref byte[] buffer, ref int bufferOffset, ref int messageOffset, ref int remainingData, ref BufferValueReader reader)
		{
			this.lastReceived = DateTime.Now;

			int length = 0;
			while (remainingData > BaseHeaderLength)
			{
				MessageHeader header = GetHeader (buffer, messageOffset);
				if (header == null)
				{
					Disconnect (true);
					int p = Interlocked.Decrement (ref this.pendingAsync);
					Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{4}, BufferMessages({0},{1},{2},{3})", buffer.Length, bufferOffset, messageOffset, remainingData, GetType().Name));
					return;
				}

				length = header.Length;
				if (length > maxMessageLength)
				{
					Disconnect (true);
					return;
				}

				if (remainingData < length)
				{
					bufferOffset += remainingData;
					break;
				}

				DeliverMessage (header, messageOffset);
				messageOffset += length;
				bufferOffset = messageOffset;
				remainingData -= length;
			}

			if (remainingData > 0 || messageOffset + BaseHeaderLength >= buffer.Length)
			{
				byte[] newBuffer = new byte[(length > buffer.Length) ? length : buffer.Length];
				reader = new BufferValueReader (newBuffer, 0, newBuffer.Length);
				Buffer.BlockCopy (buffer, messageOffset, newBuffer, 0, remainingData);
				buffer = newBuffer;
				bufferOffset = remainingData;
				messageOffset = 0;
			}
		}

		protected void ReliableReceiveCompleted (object sender, SocketAsyncEventArgs e)
		{
			int p;
			bool pending;
			do
			{
				if (e.BytesTransferred == 0 || e.SocketError != SocketError.Success)
				{
					p = Interlocked.Decrement (ref this.pendingAsync);
					Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2} ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));
					Disconnect (true);
					return;
				}

				this.rmessageLoaded += e.BytesTransferred;

				int bufferOffset = e.Offset;
				BufferMessages (ref this.rmessageBuffer, ref bufferOffset, ref this.rmessageOffset, ref this.rmessageLoaded,
				                ref this.rreader);
				e.SetBuffer (this.rmessageBuffer, bufferOffset, this.rmessageBuffer.Length - bufferOffset);
				p = Interlocked.Decrement (ref this.pendingAsync);
				Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2} ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));

				lock (this.stateSync)
				{
					if (!IsConnected)
						return;

					p = Interlocked.Increment (ref this.pendingAsync);
					Trace.WriteLine (String.Format ("Increment pending: {0}", p), String.Format ("{2} ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));
					pending = this.reliableSocket.ReceiveAsync (e);
				}
			} while (!pending);
		}

		protected DateTime lastReceived;
		protected int pingsOut = 0;

		private void DeliverMessage (MessageHeader header, int offset)
		{
			this.rreader.Position = offset + BaseHeaderLength;

			try
			{
				header.Message.ReadPayload (this.rreader);
			}
			catch
			{
				Disconnect (true);
				return;
			}

			var tmessage = (header.Message as TempestMessage);
			if (tmessage == null)
				OnMessageReceived (new MessageEventArgs (this, header.Message));
			else
				OnTempestMessageReceived (new MessageEventArgs (this, header.Message));
		}

		protected virtual void OnTempestMessageReceived (MessageEventArgs e)
		{
			switch (e.Message.MessageType)
			{
				case (ushort)TempestMessageType.Ping:
					Send (new PongMessage());
					break;

				case (ushort)TempestMessageType.Pong:
					this.pingsOut = 0;
					break;

				case (ushort)TempestMessageType.Disconnect:
					var msg = (DisconnectMessage)e.Message;
					Disconnect (true, msg.Reason);
					break;
			}
		}

		private void ReliableSendCompleted (object sender, SocketAsyncEventArgs e)
		{
			var message = (Message)e.UserToken;

			#if !NET_4
			lock (writerAsyncArgs)
			#endif
			writerAsyncArgs.Push (e);

			int p;
			if (e.BytesTransferred == 0 || e.SocketError != SocketError.Success)
			{
				p = Interlocked.Decrement (ref this.pendingAsync);
				Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2} ReliableSendCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));
				Disconnect (true);
				return;
			}

			if (!(message is TempestMessage))
				OnMessageSent (new MessageEventArgs (this, message));

			p = Interlocked.Decrement (ref this.pendingAsync);
			Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2} ReliableSendCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));
		}

		private void OnDisconnectCompleted (object sender, SocketAsyncEventArgs e)
		{
			Trace.WriteLine ("Entering", String.Format ("{2} OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));

			lock (this.stateSync)
			{
				Trace.WriteLine ("Got lock", String.Format ("{2} OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));

				this.disconnecting = false;
				Recycle();
				this.reliableSocket = null;
			}

			Trace.WriteLine ("Raising Disconnected", String.Format ("{2} OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));

			OnDisconnected (new DisconnectedEventArgs (this, this.disconnectingReason));
			int p = Interlocked.Decrement (ref this.pendingAsync);
			Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2} OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, GetType().Name));
		}

		// TODO: Better buffer limit
		private static readonly int BufferLimit = Environment.ProcessorCount * 10;
		private static volatile int bufferCount = 0;

		#if NET_4
		private static readonly ConcurrentStack<SocketAsyncEventArgs> writerAsyncArgs = new ConcurrentStack<SocketAsyncEventArgs>();
		#else
		private static readonly Stack<SocketAsyncEventArgs> writerAsyncArgs = new Stack<SocketAsyncEventArgs>();
		#endif
	}
}