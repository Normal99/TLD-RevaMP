namespace TLDRevamp
{
    internal sealed class ObjectPanelRequest
    {
        private uint _token, _round, _net;
        private double _until;
        internal void Set(uint token, uint round, uint net, double now)
        { _token = token; _round = round; _net = net; _until = now + 4; }
        // Keep delayed replies useful under latency; a closed or retargeted panel accepts none.
        internal bool Accept(uint token, uint round, uint net, double now) => net != 0 && net == _net && token == _token && unchecked(_round - round) < 8 && now <= _until;
    }
}
