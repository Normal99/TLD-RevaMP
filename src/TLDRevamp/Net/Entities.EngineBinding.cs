namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// A bolted proxy part shares the car's transform root, but never supplies that car's engine state.
        private static void BindProxyEngine(Ent e)
        {
            var engine = e.Root != null && e.Root.car != null ? e.Root.car.Engine : null;
            if (engine == null) return;
            if (e.Proxy) ProxyEngineOwner[engine] = e;
            else if (ProxyEngineOwner.TryGetValue(engine, out var owner) && owner == e) ProxyEngineOwner.Remove(engine);
        }
    }
}
