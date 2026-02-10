using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Shared
{
    public interface IMessagingService
    {
        /// <summary>
        /// Call the program with specific command and obtain the result from program.
        /// </summary>
        public object? Call(string targetId, string command, object[] args);
        /// <summary>
        /// Register a callback.
        /// </summary>
        /// <remarks>
        /// the <paramref name="method"/> will be invoked when a message has been sent to MessageServer.
        /// </remarks>
        public void RegisterCallBack(string callbackId, string callbackCommand, Func<object[], object?> method);
        public void UnRegisterCallBack(string callbackId);
        public void UnRegisterCallBack(string callbackId, string callbackCommand);

    }
}
