//
// Mono.WebServer.BaseRequestBroker
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
// 	Lluis Sanchez Gual (lluis@ximian.com)
//
// (C) Copyright 2004-2010 Novell, Inc
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;

namespace Mono.WebServer
{
	public class BaseRequestBroker: MarshalByRefObject, IRequestBroker
	{
		public event UnregisterRequestEventHandler UnregisterRequestEvent;
		
		const int MAX_REQUESTS = 65536;

		//  Contains the initial request capacity of a BaseRequestBroker
		const int INITIAL_REQUESTS = 200;

		//  The size of a request buffer in bytes.
		//
		//  This number should be equal to INPUT_BUFFER_SIZE
		//  in System.Web.HttpRequest.
		const int BUFFER_SIZE = 32*1024;
		
		// Contains a lock to use when accessing and modifying the
		// request allocation tables.
		static readonly object reqlock = new object();

		// Contains the request ID's.
		int[] request_ids = new int [INITIAL_REQUESTS];

		// Contains the registered workers.
		Worker[] requests = new Worker [INITIAL_REQUESTS];
		
		// Contains buffers for the requests to use.
		byte[][] buffers = new byte [INITIAL_REQUESTS][];
		
		// Contains the number of active requests.
		int requests_count;

		// Contains the total number of requests served so far.
		// May freely wrap around.
		uint requests_served;

		/// <summary>
		/// Grows the size of the request allocation tables by 33%.
		///
		/// This *MUST* be called with the reqlock held!
		/// </summary>
		/// <returns>Index to use for a new request.</returns>
		int GrowRequests()
		{
			var curlen = request_ids.Length;
			int newsize = curlen + curlen / 3;
			if (newsize > MAX_REQUESTS) newsize = MAX_REQUESTS;
			if (newsize == curlen)
			{
				throw new InvalidOperationException("The max requests count " + MAX_REQUESTS + " has been reached.");
			}
			var new_request_ids = new int [newsize];
			var new_requests = new Worker [newsize];
			var new_buffers = new byte [newsize][];

			request_ids.CopyTo (new_request_ids, 0);
			request_ids = new_request_ids;
			
			requests.CopyTo (new_requests, 0);
			requests = new_requests;
			
			buffers.CopyTo (new_buffers, 0);
			buffers = new_buffers;

			return curlen;
		}
		
		/// <summary>
		/// Gets the next available request ID, expanding the array
		/// of possible ID's if necessary.
		///
		/// This *MUST* be called with the reqlock held!
		/// </summary>
		/// <returns>ID of the request.</returns>
		int GetNextRequestId ()
		{
			int reqlen = request_ids.Length;

			requests_served++; // increment to 1 before putting into request_ids
					   // so that the 0 id is reserved for slot not used
			if (requests_served == 0x8000) // and check for wrap-around for the above
				requests_served = 1; // making sure we don't exceed 0x7FFF or go negative

			requests_count++;

			int emptyCellIndex;
			if (requests_count > reqlen)
				emptyCellIndex = GrowRequests();
			else
				emptyCellIndex = Array.IndexOf(request_ids, 0);

			if (emptyCellIndex == -1)
			{
				// Should never happen...
				throw new ApplicationException("could not allocate new request id");
			}

			// TODO: newid had better not exceed 0xFFFF.
			var newid = ((ushort)emptyCellIndex & 0xFFFF) | (((ushort)requests_served & 0x7FFF) << 16);
			var index = IdToIndex(newid);
			if (request_ids[index] != 0)
				throw new InvalidOperationException("The cell " + index + " is not empty for request id " + newid);
			request_ids[index] = newid;
			return newid;
		}		

		public int RegisterRequest(Worker worker)
		{
			lock (reqlock) {
				var newid = GetNextRequestId();
				var index = IdToIndex(newid);
				requests[index] = worker;
				
				// Don't create a new array if one already exists.
				byte[] a = buffers[index];
				if (a == null)
					buffers[index] = new byte[BUFFER_SIZE];

				return newid;
			}
		}

		int IdToIndex(int requestId) {
			return requestId & 0xFFFF;
		}

		public void UnregisterRequest (int id)
		{
			lock (reqlock) {
				if (!ValidRequest (id))
					return;
				
				DoUnregisterRequest (id);
				int idx = IdToIndex (id);

				byte[] a = buffers [idx];
				if (a != null)
					Array.Clear (a, 0, a.Length);
				requests [idx] = null;
				request_ids [idx] = 0;
				requests_count--;
			}
		}

		/// <summary>
		/// Invokes registered handlers of UnregisterRequestEvent. Each handler is passed an
		/// arguments object which contains the ID of a request that is about to be
		/// unregistered.
		/// </summary>
		/// <param name="id">ID of a request that is about to be unregistered.</param>
		void DoUnregisterRequest (int id)
		{
			if (UnregisterRequestEvent == null)
				return;
			Delegate[] handlers = UnregisterRequestEvent.GetInvocationList ();
			if (handlers == null || handlers.Length == 0)
				return;
			
			var args = new UnregisterRequestEventArgs (id);
			foreach (UnregisterRequestEventHandler handler in handlers)
				handler (this, args);
		}		

		protected bool ValidRequest (int requestId)
		{
			int idx = IdToIndex (requestId);
			return (idx >= 0 && idx < request_ids.Length && request_ids [idx] == requestId &&
				buffers [idx] != null);
		}		

		public int Read (int requestId, int size, out byte[] buffer)
		{
			buffer = null;
			
			Worker w;

			lock (reqlock) {
				if (!ValidRequest (requestId))
					return 0;

				w = GetWorker (requestId);
				if (w == null)
					return 0;

				// Use a pre-allocated buffer only when the size matches
				// as it will be transferred across appdomain boundaries
				// in full length
				if (size == BUFFER_SIZE) {
					buffer = buffers [IdToIndex (requestId)];
				} else {
					buffer = new byte[size];
				}
			}

			return w.Read (buffer, 0, size);
		}		

		public Worker GetWorker (int requestId)
		{
			lock (reqlock) {
				if (!ValidRequest (requestId))
					return null;
			
				return requests [IdToIndex (requestId)];
			}
		}

		public void Write (int requestId, byte[] buffer, int position, int size)
		{
			Worker worker = GetWorker (requestId);
			if (worker != null)
				worker.Write (buffer, position, size);
		}		

		public void Close (int requestId)
		{
			Worker worker = GetWorker (requestId);
			if (worker != null)
				worker.Close ();
		}		

		public void Flush (int requestId)
		{
			Worker worker = GetWorker (requestId);
			if (worker != null)
				worker.Flush ();
		}

		public bool IsConnected (int requestId)
		{
			Worker worker = GetWorker (requestId);
			
			return (worker != null && worker.IsConnected ());
		}
		
		public override object InitializeLifetimeService ()
		{
			return null;
		}
	}
}
