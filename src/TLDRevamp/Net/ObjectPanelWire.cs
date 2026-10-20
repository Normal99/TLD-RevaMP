using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    internal static class ObjectPanelWire
    {
        internal const byte Ask = 105, Query = 106, Answer = 107, Row = 108, Roster = 109;
        private static readonly NetWriter W = new NetWriter();
        internal static void Header(NetWriter w, byte type, uint token, uint round, uint net)
        { w.Reset(); w.U8(type); w.U32(token); w.U32(round); w.U32(net); }
        internal static void Request(uint token, uint round, uint net)
        {
            if (Mp.Server != null) Mp.Server.PanelAsk(0, token, round, net);
            else if (Mp.Client != null && Mp.Client.Ready)
            { Header(W, Ask, token, round, net); Mp.Client.SendRaw(W, SteamTransport.SendReliable); }
        }
        internal static void Client(byte type, NetReader r)
        {
            uint token = r.U32(), round = r.U32(), net = r.U32();
            if (r.Bad) return;
            if (type == Query)
            {
                int target = r.I32();
                if (r.Bad || r.Remaining != 0 || net == 0 || target < 0) return;
                var s = ObjectPanels.Capture(net, remote: true);
                Header(W, Answer, token, round, net); W.I32(target); s.Write(W);
                Mp.Client?.SendRaw(W, SteamTransport.SendReliable);
            }
            else if (type == Row)
            {
                int source = r.I32(); var s = ObjectPanelState.Read(r);
                if (source >= 0 && s != null && s.Net == net) ObjectPanels.Receive(token, round, source, s);
            }
            else if (type == Roster)
            {
                if (r.Remaining % 4 != 0) return;
                while (r.Remaining > 0)
                {
                    int player = r.I32();
                    if (player >= 0) ObjectPanels.Expect(token, round, net, player);
                }
            }
        }
    }

    public sealed partial class MpServer
    {
        private readonly Dictionary<int, ObjectPanelRequest> _panelRequests = new Dictionary<int, ObjectPanelRequest>();
        private readonly NetWriter _panelW = new NetWriter();
        internal void PanelMessage(int from, byte type, NetReader r)
        {
            uint token = r.U32(), round = r.U32(), net = r.U32();
            if (r.Bad) return;
            if (type == ObjectPanelWire.Ask)
            {
                if (r.Remaining == 0) PanelAsk(from, token, round, net);
                return;
            }
            int target = r.I32();
            if (!_panelRequests.TryGetValue(target, out var route) || !route.Accept(token, round, net, Time.realtimeSinceStartupAsDouble) || target == from) return;
            var s = ObjectPanelState.Read(r);
            if (s == null || s.Net != net) return;
            if (target == 0) ObjectPanels.Receive(token, round, from, s);
            else
            {
                ObjectPanelWire.Header(_panelW, ObjectPanelWire.Row, token, round, net); _panelW.I32(from); s.Write(_panelW);
                SendToPlayer(target, _panelW, true);
            }
        }
        internal void PanelAsk(int from, uint token, uint round, uint net)
        {
            if (net == 0) { _panelRequests.Remove(from); return; }
            if (!_panelRequests.TryGetValue(from, out var route)) _panelRequests[from] = route = new ObjectPanelRequest();
            route.Set(token, round, net, Time.realtimeSinceStartupAsDouble);
            if (from != 0)
            {
                var s = ObjectPanels.Capture(net, remote: true);
                ObjectPanelWire.Header(_panelW, ObjectPanelWire.Row, token, round, net); _panelW.I32(0); s.Write(_panelW); SendToPlayer(from, _panelW, true);
            }
            ObjectPanelWire.Header(_panelW, ObjectPanelWire.Roster, token, round, net);
            if (from != 0) _panelW.I32(0);
            foreach (var p in _peers.Values) if (p.Ready && p.Id != from)
            {
                if (from == 0) ObjectPanels.Expect(token, round, net, p.Id);
                else _panelW.I32(p.Id);
            }
            if (from != 0) SendToPlayer(from, _panelW, true);
            ObjectPanelWire.Header(_panelW, ObjectPanelWire.Query, token, round, net); _panelW.I32(from);
            foreach (var p in _peers.Values) if (p.Ready && p.Id != from) T.Send(p.Conn, _panelW, SteamTransport.SendReliable);
        }
    }
}
