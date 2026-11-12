using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ShinhanNativeTrader {
    // Owns event subscriptions, not the RCW. AxHost remains responsible for its OCX lifetime.
    public sealed class ComEventSubscriptions : IDisposable {
        sealed class Subscription {
            public int DispId;
            public Delegate Handler;
        }
        object source;
        readonly Guid outgoingInterface;
        readonly List<Subscription> subscriptions = new List<Subscription>();
        bool disposed;

        public ComEventSubscriptions(object source, Guid outgoingInterface) {
            if(source == null) throw new ArgumentNullException("source");
            if(!Marshal.IsComObject(source)) throw new ArgumentException("A native COM source is required.", "source");
            if(outgoingInterface == Guid.Empty) throw new ArgumentException("An outgoing event interface is required.", "outgoingInterface");
            this.source = source;
            this.outgoingInterface = outgoingInterface;
        }
        public int Count { get { return subscriptions.Count; } }
        public void AddInt32Callback(int dispId, VarEnum argumentType, Action<int> callback) {
            if(disposed) throw new ObjectDisposedException("ComEventSubscriptions");
            if(callback == null) throw new ArgumentNullException("callback");
            Delegate handler;
            if(argumentType == VarEnum.VT_I4 || argumentType == VarEnum.VT_INT)
                handler = new Action<int>(callback);
            else if(argumentType == VarEnum.VT_I2)
                handler = new Action<short>(value => callback(value));
            else throw new ArgumentException("Only scalar VT_I2, VT_I4 or VT_INT callbacks are supported.", "argumentType");
            ComEventsHelper.Combine(source,outgoingInterface,dispId,handler);
            // Keep the exact adapted delegate so Remove detaches what Combine actually advised.
            subscriptions.Add(new Subscription { DispId = dispId, Handler = handler });
        }
        public void Dispose() {
            if(disposed) return;
            disposed = true;
            bool failed = false;
            for(int i = subscriptions.Count - 1; i >= 0; i--) {
                var item = subscriptions[i];
                try { ComEventsHelper.Remove(source,outgoingInterface,item.DispId,item.Handler); }
                catch { failed = true; }
            }
            subscriptions.Clear();
            source = null;
            // Never release the RCW behind the ActiveX site. Report cleanup failure without
            // persisting provider exception text, which may contain request field values.
            if(failed) throw new InvalidOperationException("One or more COM event subscriptions could not be detached.");
        }
    }
}
