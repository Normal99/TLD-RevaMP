# API Probe

A test mod for the multiplayer mod API's second step, used by `tools/modapi2.py`. Not shipped with releases, but
the shortest example of the API: one `Plugin.cs`.

It registers a mod, one shared type (a beacon with a label in its record and a "needle" in a state record that only
players near it receive), a far store range, and one session-wide setting (`MpApi.RegisterSetting`). It has no
gameplay: the test drives it through RevaMP's debug bridge, for example `call ApiProbe.Probe.Spawn a 0 5` and
`call ApiProbe.Probe.Status`.
