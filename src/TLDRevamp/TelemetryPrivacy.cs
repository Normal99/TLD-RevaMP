using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TLDRevamp
{
    internal static class TelemetryPrivacy
    {
        internal const string Removed = "[removed]";
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ContentNames = new HashSet<string>(new[] { "bus door" }, StringComparer.Ordinal);
        private static readonly Regex ObjectSuffix = new Regex(@"(?:\(Clone\)|\s*\(\d+\)|Full)$", RegexOptions.CultureInvariant);
        private static readonly Regex Private = new Regex(
            @"\b[a-z][a-z0-9+.-]*://[^\s<>""']+|\bwww\.[^\s<>""']+|\b(?:[a-z0-9-]+\.)+[a-z]{2,}(?:/[^\s?]*)?\?[^\s]+|[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b\d{17,20}\b|\bSTEAM_\d:\d:\d+|\[U:\d:\d+\]|\b(?:account|lobby|steam)(?:id)?\s*[:= ]\s*\d+|" +
            @"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?|\[?(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.%]*(?:\]:\d+)?|" +
            @"[a-z]:[\\/][^\r\n]*|\\\\[^\r\n]*|(?:^|[\s=\(""'])/[^\r\n]*|~[/\\][^\r\n]*|%[a-z_][a-z0-9_]*%[^\r\n]*|\$\{?(?:HOME|USERPROFILE)\}?[^\r\n]*|" +
            @"\b\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:\s*(?:[+-]\d{2}:?\d{2}|[A-Z]{2,5}))?|\b\d{4}-\d{2}-\d{2}T[^\s]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        static TelemetryPrivacy()
        {
            Name(Environment.UserName); Name(Environment.MachineName);
        }
        internal static void Name(string value) { if (!string.IsNullOrWhiteSpace(value)) lock (Gate) Names.Add(value); }
        internal static void Identity(ulong value)
        {
            if (value == 0) return;
            Name(value.ToString(CultureInfo.InvariantCulture));
            if (value > uint.MaxValue) Name(((uint)value).ToString(CultureInfo.InvariantCulture));
        }
        internal static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length > 16384) return Removed;
            try
            {
                text = text.Replace(Removed, "\u0001").Replace("[player]", "\u0001");
                lock (Gate) foreach (string name in Names)
                    text = Regex.Replace(text, Regex.Escape(name), "\u0001", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                return Private.Replace(text, "\u0001").Replace("\u0001", Removed);
            }
            catch { return Removed; }
        }
        // Compiled schema tokens are not identities, even when someone chooses one as their nickname.
        internal static string Str(string value) => Json.Str(value != null && (ExportKeys.Contains(value) || Labels.Contains(value) || Commands.Contains(value) ||
            SettingKeys.Contains(value) || EventKinds.Contains(value) || Results.Contains(value) || Regex.IsMatch(value, @"^player-\d+$", RegexOptions.CultureInvariant)) ? value : Clean(value));
        internal static string RandomId(string value) => Regex.IsMatch(value ?? "", @"^(?:[0-9a-f]{16}|[0-9a-f]{32})$", RegexOptions.CultureInvariant) ? Json.Str(value) : "null";
        internal static string CodeStr(string value)
        {
            try { return value != null && value.Length <= 16384 && Symbol(value) && !Private.IsMatch(value) ? Json.Str(value) : Str(value); }
            catch { return Json.Str(Removed); }
        }
        internal static bool SessionId(string value) => Regex.IsMatch(value ?? "", "^[0-9a-f]{16}$", RegexOptions.CultureInvariant);
        internal static bool Setting(string key) => key != null && SettingKeys.Contains(key);
        internal static string SettingLine(string key, int value) => Setting(key) ? key + "=" + value.ToString(CultureInfo.InvariantCulture) + "\n" : "";
        internal static string Note(string value)
        {
            string clean = Clean(value);
            int length = Math.Min(clean.Length, 500);
            if (length > 0 && char.IsHighSurrogate(clean[length - 1])) length--;
            return clean.Substring(0, length);
        }
        private static string ObjectKey(string value)
        {
            string key = value;
            do { value = key; key = ObjectSuffix.Replace(value, ""); } while (key != value);
            return key;
        }
        // Only prefab-table hierarchies and fixed RevaMP labels may seed this vocabulary, never live instances.
        internal static void ContentName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 16384) return;
            string key = ObjectKey(value);
            if (key.Length > 0 && key.Length <= 64) lock (Gate) ContentNames.Add(key);
        }
        internal static string ObjectName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length > 16384) return Removed;
            string key = ObjectKey(value);
            lock (Gate) if (!ContentNames.Contains(key)) return Removed;
            return Clean(key) == key ? key : Removed;
        }
        // Arbitrary diagnostics have no safe vocabulary. Do not guess at identities.
        internal static string FreeText(string value) => string.IsNullOrEmpty(value) ? "" : Removed;
        internal static string Os(string value)
        {
            var m = Regex.Match(value ?? "", @"\bWindows\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success) return "Windows " + m.Groups[1].Value;
            if ((value ?? "").IndexOf("Linux", StringComparison.OrdinalIgnoreCase) >= 0) return "Linux";
            if ((value ?? "").IndexOf("Mac", StringComparison.OrdinalIgnoreCase) >= 0) return "macOS";
            return "unknown";
        }
        internal static string UtcDate() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        internal static string DateStr() => Json.Str(UtcDate());
        internal static string OsStr(string value) => Json.Str(Os(value));
        internal static string Stamp() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        internal static string Player(int index) => "player-" + index.ToString(CultureInfo.InvariantCulture);
        internal static string ReceivedName(int index, string hash, bool mark, string basename = null)
        {
            if (!Regex.IsMatch(hash ?? "", "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("content hash");
            if (index < 0 || mark && !Regex.IsMatch(basename ?? "", @"^mark-(?:[0-9a-f]{16}|local)-\d+-\d{3}(?:-[A-Za-z0-9-]{1,500})?$", RegexOptions.CultureInvariant)) throw new ArgumentException("received basename");
            return (mark ? basename : "report") + "-from-p" + index.ToString(CultureInfo.InvariantCulture) + "-" + hash.Substring(0, 8) + (mark ? ".json" : ".zip");
        }

        internal static string Bridge(string command, string json)
        {
            string fields;
            switch (command)
            {
                case "stats": fields = "frames fps p50ms p99ms p999ms maxms hitches50ms gc0 managedMB bridgeMsTotal bridgeMaxMs time"; break;
                case "mp status": fields = "server client bots remotePlayers id ready serverSeed mySeed statesIn posesIn bytesIn bytesOut ping peers statesRelayed posesRelayed batches batchedMsgs skipped tiers carStatesSent carStatesThinned carTiers rejected msgIn msgOut sendErrors count pingAvg pingMax"; break;
                case "net stats": fields = "bytesIn bytesOut msgIn msgOut sendErrors conns ping qualityLocal qualityRemote outBps inBps sendRate pendingUnreliable pendingReliable unackedReliable queueUs"; break;
                case "mp entities": fields = "me count pendingShare serverEnts statesSent statesIn stale spawned ownerChanges handoffs claimsSent claimsRefused rebound membersRebound farRecordUpdates farAdds farResyncs farResyncItems farRechunked farAttachedMoved reshared orphansCleared leasesDone leasesFreed leaseRefusedWhy malformed requesterNotReady buildingKey buildingReach buildingType buildingTypeMismatch doneMalformed doneHolder spawnShared contactSent contactRelayed contactApplied partsReported partsApplied partsAppliedStored suppressedDamage suppressedFallOff silentTakeovers entities net car owner parent part attached epoch proxy upy items driven held dormant stored shown pos delayMs jitterMs extrapolating clk rt yaw vel engRunning engRpm fuel frames extrapFrames jumps maxDevCm physical partsColOff byBody seqOut recv why sentRest lastSent lastIn"; break;
                case "mp aistats": fields = "breakHitsSent breakHitsRun breakFxSent breakFxApplied statesSent statesApplied soundsSent soundsApplied remoteSeen keptOnRemote remoteAttackFrames damageSent damageTaken"; break;
                case "mp combat": fields = "shotsTested shotHits meleeHits damageSent damageTaken damageRefused hitCheck enabled checked allowed refused noHistory objects objChecked objAllowed objRefused"; break;
                case "mp shotfx": fields = "sent applied decals holes unplaced noGun displayHoles"; break;
                case "mp blasts": fields = "sent applied reqSent reqRun copySkipped"; break;
                case "mp membergone": fields = "sent applied rejected playerDeleteSync playerDeletes memberDeletesSent memberDeletesForwarded memberDeletesApplied keepPartsOnRemove partsKept partsOrphaned partsOrphanedNoRecord"; break;
                case "mp poiusables": fields = "sent applied onSpawn unplaced known serverKnown removeDist"; break;
                default: return "null";
            }
            return Numbers(json, fields);
        }

        internal static bool Attachment(string name) => name == "stats.json" || name == "blackbox.json" || name == "breakdown.json" ||
            name == "errors.json" || name == "fixes.json" || name == "tldrevamp-settings.txt" ||
            Regex.IsMatch(name, @"^sessions/\d{8}-\d{6}-\d{3}(?:-(?:solo|host|client|dedicated))(?:-\d+)?\.jsonl$", RegexOptions.CultureInvariant);
        internal static void Zip(string directory, string path)
        {
            using (var output = File.Create(path))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string name = file.Substring(directory.Length + 1).Replace('\\', '/');
                    if (!Attachment(name)) continue;
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using (var source = File.OpenRead(file)) using (var target = entry.Open()) source.CopyTo(target);
                }
        }

        private static readonly HashSet<string> EventKinds = new HashSet<string>((
            "tpa-request-sent tpa-accept-sent tpa-refuse-sent add-applied add-assigned attach-applied attach-received attach-refused attach-request attach-sent claim-refused " +
            "claim-sent detach-applied detach-refused detach-request detach-sent edit-applied edit-refused edit-sent handover-applied handover-refused lease-refused lease-done-refused " +
            "hitch-request hitch-accept hitch-refuse hitch-refused hitch-frozen hitch-ownership-claim speed-anomaly handover-sent join joint-broken joint-created leave live-applied live-sent owner-applied owner-change " +
            "owner-decision owner-teleport physlock-lock physlock-unlock part-detached-received part-off-applied part-off-deduped part-off-refused " +
            "part-off-sent part-state player-damage player-damage-queued player-death proxy-correction proxy-snap " +
            "remove-applied remove-relayed remove-sent resync-applied resync-sent seat-enter seat-leave spawn-add speed-high " +
            "speed-jump test-desync tpa-accept-request tpa-accepted tpa-applied tpa-arrived tpa-failure tpa-pending " +
            "tpa-refuse-request tpa-refused tpa-request").Split(' '), StringComparer.Ordinal);
        internal static string EventKind(string value) => value != null && EventKinds.Contains(value) ? value : Removed;
        private static readonly HashSet<string> Results = new HashSet<string>(new[] {
            "accepted", "entity absent", "not a trailer and car", "stale epoch", "held or stored", "slot taken", "already hitched",
            "slot index", "not hitched to car", "hitch pending", "owner changed", "not loaded", "slot taken or incompatible", "joint change failed",
            "hitch changed during transfer", "old simulator frozen", "hitch handover", "hitched trailer", "hitch player handover", "hitch crash handover", "hitched to car",
            "unshared target or automatic attach", "connect", "disconnect",
            "ok", "disabled", "cooldown", "declined", "detach", "between sent states", "joint absent or parented",
            "invalid destination", "no ground after 30 s", "no local player", "arrival check superseded", "player lost before arrival",
            "within 5 m", "outside 5 m", "car or player lost", "target left", "request expired", "requester left", "missing target",
            "ambiguous target", "target absent", "ask rate limit", "target busy", "no pending request", "target position unknown",
            "teleport send failed", "requester left before send", "not connected or ready",
            "far store", "held or attach failed", "part absent",
            "parent absent", "already attached", "loader did not attach",
            "missing car, wrong owner or epoch", "car absent", "item index",
            "malformed", "missing car", "wrong owner", "epoch", "non-finite pose", "pose too far", "invalid rotation",
            "already detached or gone", "already detached", "item record", "not attached to a slot", "prefab slot", "slot parent", "not attached",
            "requester not ready", "building key", "building reach", "building type", "building type mismatch", "missing lease or wrong holder",
            "car copy had it bolted on twice", "already exists", "part absent, not in far store",
            "owner already detached", "local unbolt; sync suppressed", "timestamp",
            "missing entity or already owner", "edit rate", "edit reach",
            "malformed record", "malformed, missing item or no longer owner", "record decode failed",
            "servo release blend", "physics servo", "late, wrong epoch or no longer owner",
            "forced", "command flood limit", "cause unavailable",
            "fall", "remote creature", "remote melee",
            "remote shot", "held", "stored",
            "worn", "driven", "claim",
            "crash claim", "crash return", "hand-off (nearer player)",
            "rabbit: nearest player / moving car", "record unreadable", "usable value",
            "door value", "lamp value", "tank",
            "tank fluids", "part value", "colour edit, the item's part conditions unknown",
            "colour value", "painting", "food value",
            "ammo value", "plate", "bus doors",
            "blood: an edit only washes it off"
        }, StringComparer.Ordinal);
        internal static string EventResult(string value) => value != null && Results.Contains(value) ? value : FreeText(value);
        private static readonly HashSet<string> Commands = new HashSet<string>(new[] { "stats", "mp status", "net stats", "mp entities", "mp aistats", "mp combat", "mp shotfx", "mp blasts", "mp membergone", "mp poiusables" }, StringComparer.Ordinal);
        internal static bool ShareKey(string key) => key == "coarse:truncated" || Commands.Contains(key) || Labels.Contains(key) || ExportKeys.Contains(key) || SettingKeys.Contains(key);
        internal static bool PlainKey(string key) => key.Length > 0 && key.Length <= 64 &&
            Regex.IsMatch(key, @"\A[A-Za-z0-9_.:/-]+\z", RegexOptions.CultureInvariant);
        internal static bool PrivateKey(string key) => key == "steamId" || key == "accountId" || key == "lobbyId" || key == "port";
        private static bool Symbol(string value) => value.IndexOf('.') >= 0 && Regex.IsMatch(value, @"^[A-Za-z_<][A-Za-z0-9_.:+<>`/?-]*$", RegexOptions.CultureInvariant);
        internal static bool ShareString(string field, string value)
        {
            if (field == "sid" || field == "recording") return RandomId(value) != "null";
            if (value == "" || value == Removed || value == "[player]") return true;
            switch (field)
            {
                case "note": return value.Length <= 500 && Clean(value) == value;
                case "marksFolder": return Regex.IsMatch(value, @"^marks/\d{4}-\d{2}-\d{2}-[0-9a-f]{16}$", RegexOptions.CultureInvariant);
                case "cpu": case "gpu": return Clean(value) == value;
                case "sid": case "recording": return Regex.IsMatch(value, @"^(?:[0-9a-f]{16}|[0-9a-f]{32})$", RegexOptions.CultureInvariant);
                case "time": case "start": case "date": return Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);
                case "os": return Os(value) == value;
                case "mod": case "unity": return Regex.IsMatch(value, @"^[0-9][A-Za-z0-9_.+-]{0,119}$", RegexOptions.CultureInvariant);
                case "build": case "commit": return Regex.IsMatch(value, @"^(?:[0-9a-f]{7,64}|unknown)(?:\+changes)?$", RegexOptions.CultureInvariant);
                case "game": return value == "beta" || value == "unknown" || Regex.IsMatch(value, "^[0-9a-f]{12}$", RegexOptions.CultureInvariant);
                case "screen": return Regex.IsMatch(value, @"^\d+x\d+$", RegexOptions.CultureInvariant);
                case "k": return value == "head" || value == "tick" || value == "hitch" || value == "warn" || value == "load" || value == "save" || value == "mark";
                case "role": return value == "solo" || value == "host" || value == "client" || value == "dedicated";
                case "stSource": return value == "host" || value == "local";
                case "by": return value == "me" || value == "host" || value == "player";
                case "stage": return value == "firstUsable" || value == "firstState";
                case "kind": return EventKinds.Contains(value) || value == "warning" || value == "error" || value == "exception" || value == "new" || value == "load" || value == "join";
                case "result": return Results.Contains(value);
                case "name": return ObjectName(value) == value || Regex.IsMatch(value, @"^#\d{1,3}$", RegexOptions.CultureInvariant) || value.Split('/').Length > 1 && Array.TrueForAll(value.Split('/'), v => Labels.Contains(v)) || Labels.Contains(value) || Regex.IsMatch(value, @"^player-\d+$", RegexOptions.CultureInvariant);
                case "type": case "content": return ObjectName(value) == value;
                case "cols": case "m": case "p": return value == "coarse:truncated" || ExportKeys.Contains(value) || Labels.Contains(value) || Symbol(value);
                default: return false;
            }
        }
        internal static void CheckSettings(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq < 1 || !SettingKeys.Contains(line.Substring(0, eq)) || !int.TryParse(line.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new InvalidDataException("private settings text");
            }
        }

        private static readonly HashSet<string> ExportKeys = new HashSet<string>((
            "date crosshair dragging closestPlayer distance origin marksFolder bandwidth identical corpseRoot unknown creatureRange deadAnimation idlePose physlocked attachedPart rabbitAnimation ragdollPose " +
            "commit head tick hitch warn load save mark warning error exception solo host client dedicated local me player " +
            "new firstUsable firstState xCm yCm zCm interval age mode correction activated ageS ai alloc allocBytesPerFrame " +
            "allocCounter allocKBps allocProbeOverheadNs allowed am api applied applyHz at attached auto avgAll avgMsAll " +
            "avgMsGcFrames avgMsNoGcFrames avgMsSlow avgSlow awakeRigidbodies baseNs batchedMsgs batches blackboxFrames " +
            "bodiesAdded bodiesDropped bots bound breakFxApplied breakFxSent breakHitsRun breakHitsSent bridge bridgeMaxMs " +
            "bridgeMsTotal budgetBytes build building buildings buildingsDeferred busDoor busyMs by byAlloc byBody " +
            "bySlowFrames bySpike byTime bytes bytesIn bytesOut bytesPerFrame bytesReceived bytesSent callLimit calls " +
            "callsPerFrame callsPerSec captureMs car carCopies carStatesSent carStatesThinned carTiers cars carsNearby " +
            "categories centerCm checked claimsRefused claimsSent client clip clk clock cloud coarseCallLimit coarseTimers " +
            "cols columns combat complete condition condition2 condition3 connectS conns contactApplied contactRelayed " +
            "contactSent content contentItem contentNet copySkipped cores count countersValid counts cpu damageRefused " +
            "damageSent damageTaken day decals delayMs destroyed displayHoles dormant drawCalls driven driving dropped " +
            "droppedKinds dx dy dz enabled engRpm engRunning engine entities epoch error errors events exceptions " +
            "extrapFrames extrapolating failed farAdds farAttachedMoved farRechunked farRecordUpdates farResyncItems " +
            "farResyncs firstStateS firstUsableS fixes focus focusDropped focused format fps frame frames from fuel " +
            "fullscreen game gc gc0 gc1 gc2 gcFrames gen genDone gpu gpuBound gpuBoundFrames gpuMB gpuMs handleMs handoffs " +
            "hasCenter hasPos head heapGrowthBytes height held hitCheck hitchCount hitchMs hitches hitches50 hitches50ms " +
            "hitchesDropped holes hw id ignition il inBps inBytes inUse inUseMulti index instantiated inventory " +
            "inventoryComplete inventoryCount item items jitterMs jumps k kbPerFrame kbPerS kbPerSec keepPartsOnRemove " +
            "keptOnRemote key kind kinds known lastIn lastSent leasesDone leasesFreed leaseRefusedWhy malformed requesterNotReady buildingKey buildingReach buildingType buildingTypeMismatch doneMalformed doneHolder life lights live loading local " +
            "localItem localPlayer locked long longFailed longHitchesDropped loop m mainThreadMs managedMB markers maxBytes " +
            "maxDevCm maxMs maxms me meleeHits memberDeletesApplied memberDeletesForwarded memberDeletesSent membersRebound " +
            "missing mod momentSt monobehaviours mp ms msPerFrame msPerFrameAll msPerSec msPerSlowFrame msg msgIn msgOut " +
            "mySeed n name net network noGun noHistory note objAllowed objChecked objRefused objects ok on onSpawn " +
            "orphansCleared os other outBps outBytes outside over5ms overheadNs overwritten owned owner ownerChanges p p50ms " +
            "p95ms p999ms p99ms parent part particleSystems particleSystemsPlaying partsApplied partsAppliedStored " +
            "partsColOff partsKept partsOrphaned partsOrphanedNoRecord partsReported patched path peers peersDropped pending " +
            "pendingReliable pendingShare pendingUnreliable perFrame phases physical physicsAllOwned physicsSkipped mountPosCm mountRot crafted ping " +
            "pingAvg pingMax player playerDeleteSync playerDeletes playerPos players poi pos posCm poseSt posesIn " +
            "posesRelayed prefab privacy probedNs probes proxy quality qualityLocal qualityRemote queueUs radio radioAm " +
            "radioOn radiusM ramMB rates ready readyAfterS rebase rebaseMoved rebound received receivedHz recording recv " +
            "refused rejected remoteAttackFrames remotePlayers remoteSeen removeDist render renderThreadMs renderers " +
            "renderersVisible reqRun reqSent reshared result revamp rigidbodies rigidbodiesAwake rigidbodiesDynamic roads " +
            "role rows rt sampleAgeS sampleSt sampled sand saving scanMs scene scenery screen scriptTimers scripts seat seats " +
            "seconds seed selfAllocBytesPerFrame selfMs sendErrors sendHz sendRate sent sentHz sentRest seqOut server " +
            "serverEnts serverKnown serverSeed setPass settings shadowCasters shadowLights shotHits shotsTested shown sid " +
            "silentTakeovers size skipped slide slot slots slowFrameMs slowFrames slowGpuBound slowThresholdMs slowWithGc " +
            "soundsApplied soundsSent spawnMethods spawnShared spawnSkipped spawned speed speedKmh st stSource stage stale " +
            "start state statesApplied statesIn statesRelayed statesSent stations stored storms stream suppressedDamage " +
            "suppressedFallOff system t targetFps telemetry telemetrySnapshotMs terrain tiers time timerAlloc timerColumns " +
            "timers to top totalKbPerSec totalMsPerFrame totalMsPerSec trace trianglesK tuned turn type types unackedReliable " +
            "unity unityAllocMB unityReservedMB unplaced upy usables valid value vel viewDistance vramMB vsync wall warnings " +
            "what why width worldDataS x y yaw").Split(' '), StringComparer.Ordinal);
        private static readonly HashSet<string> Labels = new HashSet<string>(new[] {
            "TimeUpdate", "Initialization", "EarlyUpdate", "FixedUpdate", "PreUpdate", "Update", "PreLateUpdate",
            "PostLateUpdate", "?", "AI/munka", "Add", "AiSound", "AiState", "Assigned", "AttachReq", "AttachSync", "Batch",
            "Batches Count", "BehaviourUpdate", "BreakFx", "BreakHit", "CPU Main Thread Frame Time",
            "CPU Render Thread Frame Time", "Camera.Render", "CensusAsk", "CensusReply", "Chat", "Claim", "CompactState", "ContactImpulse",
            "CrashClaim", "CrashRelease", "DetachReq", "DetachSync", "Draw Calls Count", "Edit", "ExplodeReq",
            "ExplosionFx", "FarHz", "FixedBehaviourUpdate", "GC.Collect", "GPU Frame Time", "Gfx.PresentFrame",
            "Gfx.WaitForPresentOnGfxThread", "GoMsg", "HandoverState", "Hello", "IgnitionKey", "JoinSpot",
            "LateBehaviourUpdate", "LeaseDone", "LeaseFreed", "LeaseGrant", "LeaseReq", "LeaseTaken", "LiveUsable",
            "Loading.UpdatePreloading", "Main Thread", "MemberGone", "Mesh.UploadMeshData", "MidHz", "MidM", "ModAdd",
            "ModAssigned", "ModChunk", "ModClaim", "ModClock", "ModEdit", "ModEvent", "ModFetch", "ModOffer", "ModOwner",
            "ModPose", "ModRecord", "ModRemove", "ModSetting", "ModShare", "ModState", "ModStore", "NearHz", "NearM",
            "OtherOutfit", "OtherPose", "OtherState", "Outfit", "Owner", "PartDetached", "PartOff", "PhysLockSync",
            "Physics.BakeCollisionMeshes", "Physics.Processing", "Physics.Simulate", "PlayerDamage", "PlayerJoined",
            "PlayerLeft", "PlayerLoop", "PlayerSave.Record", "PlayerSave.Restore", "PlayerSound", "PlayerState",
            "PoiCensus", "PoiUsable", "Pose", "PushIn", "RabbitFx", "RabbitState", "RadioSync", "Ready", "Refetch",
            "Reject", "RejectFull", "RejectKicked", "RejectOther", "RejectPassword", "RemoveItem",
            "RenderPipelineManager.DoRenderLoop_Internal", "ReportAsk", "ReportChunk", "Resync", "Roster",
            "SetPass Calls Count", "Shadow Casters Count", "Share", "ShareState", "SharedChunk", "ShotFx", "ShotgunIn",
            "SleepSync", "SpotReq", "State", "StateHz", "StormSync", "TelemetryMark", "TelemetryMarks.Message",
            "Triangles Count", "Version", "Voice", "Welcome", "WorldClock", "Worn", "car", "idx", "item", "net", "other",
            "player", "rabbit/animal", "ragdoll/corpse", "trailer"
        }, StringComparer.Ordinal);
        private static readonly HashSet<string> SettingKeys = new HashSet<string>((
            "mirrors mirrorShadows tinyParts grass shadowCascades reflections smallItemShadows binoculars roadPlace scenery " +
            "sceneryConsistent roadConnect roadChunks terrain parallelRoads lists itemSpawn itemPlace partCondition wheels " +
            "impactSounds worldShift buildingRoads aiRays outfitGarbage textureStreaming pour carParticles trailerBrakes " +
            "swayAssist rollAssist tractionAssist sandstorms voice voiceVolume tpa overlay netPackets shareReports askReports").Split(' '), StringComparer.Ordinal);

        // A schema projection, rather than a recursive 'keep all numbers': ports and account ids are numbers too.
        internal static string Numbers(string json, string fields)
        {
            try { return new Projection(json, fields).Read(); } catch { return "null"; }
        }
        private sealed class Projection
        {
            private readonly string _s;
            private readonly HashSet<string> _fields;
            private int _at;
            internal Projection(string s, string fields) { _s = s ?? ""; _fields = new HashSet<string>(fields.Split(' '), StringComparer.Ordinal); }
            private char Peek => _at < _s.Length ? _s[_at] : '\0';
            private void Space() { while (Peek == ' ' || Peek == '\t' || Peek == '\r' || Peek == '\n') _at++; }
            private bool Take(char c) { Space(); if (Peek != c) return false; _at++; return true; }
            private void Need(char c) { if (!Take(c)) throw new FormatException(); }
            internal string Read() { var result = Value(0); Space(); if (_at != _s.Length) throw new FormatException(); return result; }
            private string String()
            {
                Need('"'); var b = new StringBuilder();
                while (_at < _s.Length)
                {
                    char c = _s[_at++]; if (c == '"') return b.ToString();
                    if (c < 32) throw new FormatException();
                    if (c == '\\')
                    {
                        if (_at == _s.Length) throw new FormatException(); c = _s[_at++];
                        switch (c)
                        {
                            case '"': case '\\': case '/': break;
                            case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break; case 'r': c = '\r'; break; case 't': c = '\t'; break;
                            case 'u': c = (char)int.Parse(_s.Substring(_at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture); _at += 4; break;
                            default: throw new FormatException();
                        }
                    }
                    b.Append(c);
                }
                throw new FormatException();
            }
            private string Value(int depth)
            {
                if (depth > 64 || _s.Length > 16 << 20) throw new FormatException();
                Space();
                if (Take('{'))
                {
                    var b = new StringBuilder("{"); bool comma = false;
                    if (!Take('}'))
                    {
                        do
                        {
                            string key = String(); Need(':'); string value = Value(depth + 1);
                            if (!_fields.Contains(key)) continue;
                            if (comma) b.Append(','); comma = true;
                            b.Append(Str(key)).Append(':').Append(value);
                        } while (Take(','));
                        Need('}');
                    }
                    return b.Append('}').ToString();
                }
                if (Take('['))
                {
                    var b = new StringBuilder("["); bool comma = false;
                    if (!Take(']'))
                    {
                        do { if (comma) b.Append(','); comma = true; b.Append(Value(depth + 1)); } while (Take(',')); Need(']');
                    }
                    return b.Append(']').ToString();
                }
                if (Peek == '"') { String(); return "null"; }
                int start = _at;
                while (Peek != '\0' && Peek != ',' && Peek != '}' && Peek != ']' && !char.IsWhiteSpace(Peek)) _at++;
                string token = _s.Substring(start, _at - start);
                if (token == "true" || token == "false" || token == "null") return token;
                if (!Regex.IsMatch(token, @"^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$", RegexOptions.CultureInvariant) ||
                    !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || double.IsInfinity(number)) throw new FormatException();
                return Math.Abs(number) >= 1e15 ? "null" : token;
            }
        }
    }
}
