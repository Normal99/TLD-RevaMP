using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TLDRevamp.Net;

namespace TLDModEvidence
{
    /// <summary>The rules the #39/#40 evidence lane has to decide, in one place.</summary>
    /// <remarks>
    /// These are the decisions the issue threads left open. They are pure functions over plain data so
    /// that both the offline harness and the live run can use the same rule, and so that a rule change
    /// shows up as a failing test rather than as a different answer in two places.
    ///
    /// Nothing here touches the game, the network, or the filesystem: the rules decide, the harness proves.
    /// </remarks>
    public static class Policy
    {
        /// <summary>How a mod's watermark is decided.</summary>
        public enum WipScope
        {
            /// <summary>The mark is shown on every build and every branch (issue #40: "all branches or not?" -> yes).</summary>
            AllBranches,
            /// <summary>The mark is shown only on development branches, never on a release.</summary>
            DevelopmentOnly,
            /// <summary>The mark is never shown.</summary>
            Never,
        }

        /// <summary>One build's identity: which branch it came from and whether it is a release.</summary>
        public sealed class BuildInfo
        {
            public string Branch = "";
            public string Commit = "";
            public bool Dirty;
            public bool Release;
            public string Version = "";

            public bool IsDevelopmentBranch
            {
                get
                {
                    if (Release) return false;
                    string b = (Branch ?? "").Trim().ToLowerInvariant();
                    if (b.Length == 0) return true;   // no branch recorded: a local build is a development build
                    return b != "main" && b != "master" && b != "release" && b != "stable";
                }
            }
        }

        /// <summary>One mod release, as the catalog describes it.</summary>
        public sealed class ModRelease
        {
            public string Id = "";
            public string Version = "";
            public long Size;
            public string Hash = "";
            public string File = "";
            public string Revamp = "";
            /// <summary>The release URL. Only GitHub release assets are accepted (the production rule).</summary>
            public string Url = "https://github.com/owner/repo/releases/download/v1/mod.dll";
        }

        // ---- the WIP mark's screen scaling -------------------------------------------------------------------
        //
        // "Scaling" for #40 is the *mark's* scaling, not a mod's assets: the mark is a fixed 180x24
        // rectangle anchored to Screen.width/Screen.height, and the audit's F18 found that in
        // single-pass-instanced VR Screen.width is the PER-EYE width, so the label lands in the
        // middle of the right eye instead of in a corner.
        //
        // The rule, in one place:
        //   * the mark is anchored to the *eye* rectangle, not the whole screen, when the eye
        //     rectangle is known (VR);
        //   * the mark's size is a fraction of the anchor, clamped, so it neither overflows a
        //     small window nor becomes unreadable on a large one;
        //   * the mark always sits in the lower-right of its anchor and always fits inside it.

        /// <summary>The mark's width as a fraction of the anchor's width.</summary>
        public const float MarkWidthFraction = 0.14f;
        /// <summary>The mark's height as a fraction of the anchor's height.</summary>
        public const float MarkHeightFraction = 0.045f;
        /// <summary>The margin from the anchor's edges, as a fraction of the anchor's width.</summary>
        public const float MarkMarginFraction = 0.012f;
        /// <summary>The mark is never smaller than this, so it stays readable.</summary>
        public const float MarkMinWidth = 120f;
        /// <summary>The mark is never larger than this.</summary>
        public const float MarkMaxWidth = 260f;

        /// <summary>The rectangle the mark is drawn in, for a given anchor.</summary>
        public static MarkRect MarkRectFor(float anchorWidth, float anchorHeight, bool stereo)
        {
            float w = anchorWidth * MarkWidthFraction;
            float h = anchorHeight * MarkHeightFraction;
            if (w < MarkMinWidth) w = MarkMinWidth;
            if (w > MarkMaxWidth) w = MarkMaxWidth;
            float margin = anchorWidth * MarkMarginFraction;
            float left = anchorWidth - w - margin;
            float top = anchorHeight - h - margin;
            // An anchor too small for the minimum mark gets a mark that fills it: the readability
            // floor cannot be met by an anchor that cannot hold it, and a mark outside its anchor
            // is worse than a small one. The rule prefers to fit.
            if (w > anchorWidth) { w = anchorWidth; left = 0; }
            if (h > anchorHeight) { h = anchorHeight; top = 0; }
            if (left < 0) { left = 0; w = anchorWidth; }
            if (top < 0) { top = 0; h = anchorHeight; }
            if (w > anchorWidth) w = anchorWidth;
            if (h > anchorHeight) h = anchorHeight;
            return new MarkRect(left, top, left + w, top + h, stereo);
        }

        /// <summary>Why a mark rectangle is refused, or null when it is allowed.</summary>
        public static string MarkRectRefusalReason(MarkRect rect, float anchorWidth, float anchorHeight)
        {
            if (rect.Left < 0 || rect.Top < 0) return "The mark starts outside its anchor.";
            if (rect.Right > anchorWidth + 0.01f) return "The mark ends outside its anchor.";
            if (rect.Bottom > anchorHeight + 0.01f) return "The mark ends below its anchor.";
            if (rect.Width <= 0 || rect.Height <= 0) return "The mark has no size.";
            // The width bounds only apply when the anchor could hold them: a mark that fills an
            // anchor smaller than the minimum is as readable as that anchor allows.
            if (rect.Width < MarkMinWidth && anchorWidth >= MarkMinWidth) return "The mark is too narrow to read.";
            if (rect.Width > MarkMaxWidth && anchorWidth > MarkMaxWidth) return "The mark is too wide.";
            return null;
        }

        /// <summary>A well-formed SHA-256 (the production rule is \A[0-9a-f]{64}\z).</summary>
        public const string GoodHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        /// <summary>Every reason a mod release is refused, in a stable order. An empty list means it is allowed.</summary>
        /// <remarks>
        /// The manifest and signature part of the decision is **not** re-implemented here: it is the production
        /// <c>ModCatalog.ApprovalReason</c>, called directly. This method only adds the scaling rule on top,
        /// so the two can never disagree about the signature.
        /// </remarks>
        public static List<string> ReleaseRefusals(ModRelease release, IDictionary<string, string> approved, long maxBytes, Func<string, bool> validId)
        {
            var reasons = new List<string>();
            if (release == null) { reasons.Add("There is no release."); return reasons; }

            // 1. The manifest and the signature: the production decision.
            var catalogMod = new ModCatalog.Mod { Id = release.Id, Name = release.Id, Versions = new List<ModCatalog.Release>() };
            var catalogRelease = new ModCatalog.Release
            {
                Version = release.Version,
                Url = release.Url,
                File = release.File,
                Hash = release.Hash,
                Size = release.Size,
                Revamp = release.Revamp,
            };
            catalogMod.Versions.Add(catalogRelease);
            string why = ModCatalog.ApprovalReason(catalogMod, catalogRelease, approved ?? new Dictionary<string, string>(), (int)maxBytes, validId);
            if (why != null) reasons.Add(why);

            return reasons;
        }

        /// <summary>Whether the WIP watermark must be drawn for a build.</summary>
        public static bool WipMarkVisible(WipScope scope, BuildInfo build)
        {
            if (scope == WipScope.Never) return false;
            if (build == null) return scope == WipScope.AllBranches;
            if (scope == WipScope.AllBranches) return true;
            return build.IsDevelopmentBranch;
        }

        /// <summary>The text the watermark draws, including the build it belongs to.</summary>
        /// <remarks>
        /// A WIP mark is meant for videos, so it has to say *which* build is on screen. The overlay's own
        /// performance line shows "TLD Revamp {Version} ({CommitLabel})"; the mark uses the same identity so a
        /// screenshot identifies the build without a second source of truth.
        /// </remarks>
        public static string WipMarkText(WipScope scope, BuildInfo build)
        {
            if (!WipMarkVisible(scope, build)) return "";
            if (build == null) return "TLD Revamp · WIP";
            string version = build.Version ?? "";
            string label = build.CommitLabel();
            string identity = string.IsNullOrEmpty(version) ? label : version + " (" + label + ")";
            return "TLD Revamp · WIP " + identity;
        }

        /// <summary>Whether a branch may be used for a test copy. A release branch is never a test branch.</summary>
        /// <remarks>
        /// The branch may be a full ref ("origin/release") or a bare name ("release"). The comparison is on the
        /// last path segment, because that is the branch's own name.
        /// </remarks>
        public static bool BranchAllowedForTest(string branch)
        {
            string b = (branch ?? "").Trim().ToLowerInvariant();
            if (b.Length == 0) return false;
            string name = b.Contains("/") ? b.Substring(b.LastIndexOf('/') + 1) : b;
            if (name.Length == 0) return false;
            return name != "release" && name != "stable" && name != "production" && name != "prod";
        }

        /// <summary>Whether a copy may be used by the harness. Only an isolated test copy with its own prefix qualifies.</summary>
        public static bool CopyAllowed(string kind, string compat, string prefixRoot)
        {
            if (kind == null) return false;
            string k = kind.Trim().ToLowerInvariant();
            if (k != "test") return false;
            if (string.IsNullOrEmpty(compat)) return false;          // a copy without its own prefix would share one
            if (string.IsNullOrEmpty(prefixRoot)) return false;
            return compat.StartsWith(prefixRoot, StringComparison.Ordinal) && !compat.Contains("..");
        }
    }

    /// <summary>Where the mark is drawn. <see cref="Stereo"/> says whether the anchor was an eye.</summary>
    public readonly struct MarkRect
    {
        public readonly float Left, Top, Right, Bottom;
        public readonly bool Stereo;
        public MarkRect(float left, float top, float right, float bottom, bool stereo)
        { Left = left; Top = top; Right = right; Bottom = bottom; Stereo = stereo; }
        public float Width => Right - Left;
        public float Height => Bottom - Top;
    }

    public static class BuildInfoExtensions
    {
        /// <summary>The short label the overlay shows next to the version.</summary>
        public static string CommitLabel(this Policy.BuildInfo build)
        {
            if (build == null) return "unknown";
            string sha = build.Commit ?? "";
            string label = sha.Length == 40 ? sha.Substring(0, 7) : "unknown";
            return label + (build.Dirty ? "+changes" : "");
        }
    }
}
