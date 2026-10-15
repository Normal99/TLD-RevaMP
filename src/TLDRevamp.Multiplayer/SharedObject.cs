using System;
using UnityEngine;

namespace TLDRevamp.Multiplayer
{
    /// A shared object, on the machine that made it and on every machine with a copy.
    public sealed class SharedObject
    {
        /// The session-wide id. 0 until the host has assigned one (a few hundred milliseconds after Share).
        public uint Id { get; internal set; }
        public SharedType Type { get; internal set; }
        public GameObject GameObject { get; internal set; }

        /// The player whose machine owns it (0 = the host).
        public int Owner { get; internal set; }
        public bool IsMine => Owner == MpApi.MyPlayerId;
        /// Removed (by its owner, or the session ended for this copy).
        public bool Removed { get; internal set; }

        /// The mod's own data for this object on this machine (never sent).
        public object Tag;

        internal uint Epoch;          // owner changes: messages from an older owner are ignored
        internal uint LocalTag;       // the creator's handle until the host assigns an id
        internal bool RecordDirty;
        internal bool Pending => Id == 0;

        /// Owner: the state changed in a way copies must see; a new record goes out (at most a few per second, merged).
        public void RecordChanged()
        {
            if (!IsMine || Removed) return;
            RecordDirty = true;
        }

        /// Ask the owner to change something. The edit is what the type's OnEdit reads. On the owner's own machine the
        /// edit is applied at once (OnEdit runs here). Returns false if it was refused before sending (too big, not in a
        /// session, removed).
        public bool RequestEdit(Action<RecordWriter> write) => MpApi.Backend?.RequestEdit(this, write) ?? LocalEdit(write);

        /// Ask to become the owner (the type must allow it). OnOwnerChanged tells when it happened.
        public void RequestOwnership() => MpApi.Backend?.RequestOwnership(this);

        /// Remove the object everywhere. Only the owner can (on a copy this does nothing: send the owner an edit that
        /// makes it call Remove). OnRemoved runs on every machine, this one too.
        public void Remove() => MpApi.Backend?.Remove(this);

        // no session (TLD Revamp missing, or single player): edits are applied here, as the owner would
        private bool LocalEdit(Action<RecordWriter> write)
        {
            if (Removed || Type == null) return false;
            var w = new RecordWriter();
            write(w);
            return Type.OnEdit(this, new RecordReader(w.Buf, 0, w.Length), MpApi.MyPlayerId);
        }

        public override string ToString() => $"{Type?.Key ?? "?"}#{Id} (owner {Owner})";
    }

    /// A mod's identity in the session. Every player needs the same mods (id, version and file hash), checked at join.
    public sealed class ModInfo
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string Version { get; internal set; }
        /// SHA-256 of the mod's DLL file (hex), computed at registration.
        public string Hash { get; internal set; }
        public string File { get; internal set; }

        public override string ToString() => $"{Name} {Version} ({Id})";
    }

    /// Who receives a mod event.
    public enum EventTarget : byte
    {
        /// Every player, also the sender.
        All = 0,
        /// Every player but the sender.
        Others = 1,
        /// Only the host.
        Host = 2,
        /// Only the owner of the object the event is about.
        Owner = 3,
    }

    /// A mod event received from a player.
    public readonly struct ModEvent
    {
        public readonly int From;
        public readonly string Name;
        /// The object it is about, or null.
        public readonly SharedObject About;
        public readonly RecordReader Data;

        public ModEvent(int from, string name, SharedObject about, RecordReader data)
        {
            From = from; Name = name; About = about; Data = data;
        }
    }

    /// A player in the session.
    public readonly struct PlayerInfo
    {
        public readonly int Id;
        public readonly string Name;
        public PlayerInfo(int id, string name) { Id = id; Name = name; }
    }
}
