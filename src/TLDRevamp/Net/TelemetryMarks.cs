using System;
using UnityEngine;

namespace TLDRevamp.Net
{
    internal static class TelemetryMarks
    {
        // One new message: requests carry note; broadcasts carry note, host time, anonymous origin and a self bit.
        internal const byte Message = 92;
        private static readonly MarkLimiter Limit = new MarkLimiter();
        internal static void Reset() => Limit.Clear();
        internal static string Note(string note)
        {
            string clean = TelemetryPrivacy.Clean(Session.Clean(note));
            return clean.Length > 60 ? clean.Substring(0, 60) : clean;
        }
        internal static string Local(string note)
        {
            note = Note(note);
            if (Mp.Server != null) return Server(0, 0, note) ? "{\"ok\":true}" : "{\"error\":\"one mark per 10 seconds\"}";
            if (!Limit.Try(0, Time.realtimeSinceStartupAsDouble)) return "{\"error\":\"one mark per 10 seconds\"}";
            if (Mp.Client != null)
            {
                if (!Mp.Client.Ready) return "{\"error\":\"not ready\"}";
                var w = new NetWriter(); w.U8(Message); w.Str(note); Mp.Client.SendRaw(w, SteamTransport.SendReliable);
            }
            else SessionLog.Mark(note, "me", SessionLog.St);
            return "{\"ok\":true}";
        }
        internal static bool Server(int from, ulong account, string note)
        {
            ulong key = from == 0 ? 0 : account != 0 ? account : 0xFFFFFFFF00000000UL | (uint)from;
            if (!Limit.Try(key, Time.realtimeSinceStartupAsDouble)) return false;
            note = Note(note); double st = SessionLog.St;
            SessionLog.Mark(note, from == 0 ? "me" : "player", st);
            Mp.Server.SendMark(from, note, st); return true;
        }
        internal static void Client(NetReader r)
        {
            string note = r.Str(); double st = r.F64(); byte origin = r.U8(), self = r.U8();
            if (r.Bad || double.IsNaN(st) || double.IsInfinity(st) || origin > 1 || self > 1) return;
            SessionLog.Mark(Note(note), self == 1 ? "me" : origin == 0 ? "host" : "player", st);
        }
    }
    public sealed partial class MpServer
    {
        private bool MarkCommand(int from, string text)
        {
            if (!text.Equals("/mark", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("/mark ", StringComparison.OrdinalIgnoreCase)) return false;
            ulong who = ById(from, out var p) ? p.SaveId : 0;
            if (!TelemetryMarks.Server(from, who, text.Length > 5 ? text.Substring(6) : "")) Tell(from, "One /mark per player every 10 seconds.");
            return true;
        }
        internal void MarkRequest(int from, ulong account, NetReader r)
        {
            string note = r.Str();
            if (!r.Bad && !TelemetryMarks.Server(from, account, note)) Tell(from, "One /mark per player every 10 seconds.");
        }
        internal void SendMark(int from, string note, double st)
        {
            foreach (var p in _peers.Values) if (p.Ready)
            {
                _w.Reset(); _w.U8(TelemetryMarks.Message); _w.Str(note); _w.F64(st); _w.U8(from == 0 ? (byte)0 : (byte)1); _w.U8(p.Id == from ? (byte)1 : (byte)0);
                T.Send(p.Conn, _w, SteamTransport.SendReliable);
            }
        }
    }
}
