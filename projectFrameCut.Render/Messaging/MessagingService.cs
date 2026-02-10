using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.Messaging
{
    public class GeneralMessagingService : IMessagingService
    {
        ConcurrentDictionary<string, MessagingUsers> users = new ConcurrentDictionary<string, MessagingUsers>();

        public object? Call(string targetId, string command, object[] args)
        {
            if(users.TryGetValue(targetId, out var user))
            {
                if(user.Callbacks.TryGetValue(command, out var method))
                {
                    return method.Invoke(args);
                }
            }
            return null;
        }

        public void RegisterCallBack(string callbackerId, string callbackCommand, Func<object[], object?> method)
        {
            users.AddOrUpdate(callbackerId,
                (id) => new MessagingUsers
                {
                    Id = id,
                    Callbacks = new Dictionary<string, Func<object[], object?>>
                    {
                        { callbackCommand, method }
                    }
                },
                (id, existingUser) =>
                {
                    existingUser.Callbacks[callbackCommand] = method;
                    return existingUser;
                });
        }

        public void UnRegisterCallBack(string callbackId)
        {
            users.TryRemove(callbackId, out var _);
        }

        public void UnRegisterCallBack(string callbackId, string callbackCommand)
        {
            if(users.TryGetValue(callbackId, out var user))
            {
                user.Callbacks.Remove(callbackCommand);
            }
        }

        class MessagingUsers
        {
            public string Id { get; set; }
            public Dictionary<string, Func<object[],object?>> Callbacks { get; set; }
        }
    }



    
}
