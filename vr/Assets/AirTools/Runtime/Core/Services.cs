using System;
using System.Collections.Generic;

namespace AirTools.Core
{
    /// Tiny service locator: scene singletons register themselves in OnEnable, others look them up.
    public static class Services
    {
        static readonly Dictionary<Type, object> s_Services = new Dictionary<Type, object>();

        public static void Register<T>(T service) where T : class => s_Services[typeof(T)] = service;

        public static void Unregister<T>(T service) where T : class
        {
            if (s_Services.TryGetValue(typeof(T), out var current) && ReferenceEquals(current, service))
                s_Services.Remove(typeof(T));
        }

        public static T Get<T>() where T : class => s_Services.TryGetValue(typeof(T), out var s) ? (T)s : null;

        public static bool TryGet<T>(out T service) where T : class
        {
            service = Get<T>();
            return service != null;
        }

        public static void Clear() => s_Services.Clear();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad() => Clear();
    }
}
