# Local deployment and PP calculation

Official osu! requests default to `osu/current` for all four rulesets. The wrapper
pins `ppy.osu.Game` and the ruleset packages to **2026.1026.0**, matching
[ppy/osu-queue-score-statistics](https://github.com/ppy/osu-queue-score-statistics/blob/master/osu.Server.Queues.ScoreStatisticsProcessor/osu.Server.Queues.ScoreStatisticsProcessor.csproj)
when checked on 2026-11-08. Explicit historical calculators and `rosu` remain
available; ppysb retains its own calculator. These pinned packages must be reviewed
when ppy changes its deployed rulesets. This is not automatic PP version tracking.

The upgrade was checked against 15 public scores covering osu!, taiko, catch and
mania, including stable and lazer scores. The default calculator matched an
independent runner of the official packages within floating-point precision. The
largest difference from the stored official API PP was 0.015626; the official
processor deliberately skips rewriting existing PP for differences below 0.1.
The native image renderer, image quality and fonts were preserved. When feeding
identical values to the renderer, six score images were byte-identical to the
previous deployment, and the profile image only differed in its timestamp.

When IAM is disabled, OneBot account resolution and `!bind <osu! username>` use
the existing local database. When IAM is enabled, the verification-code flow is
unchanged. OAuth tokens and the full configuration are not written to startup logs.
TOML configuration serialization retains its concrete type so saved settings can
be loaded again.

Derived beatmap data uses a new `osu_standard_beatmap_tech_data_v2` table with a
bounded composite key for beatmap, calculator and mods. The official calculator's
assembly version is part of the cache key, preventing reuse across PP upgrades.
Startup creates missing tables. Existing accounts and the old derived cache table
are retained; rolling back the executable therefore does not require restoring
account data. Temporary Windows file locks on image cache metadata are retried
without replacing a good cached image with partial data.

For an upgrade, first run the contract and calculator tests, compare rendered
fixtures, then exercise the actual published executable against a dedicated test
database and loopback OneBot port. Each simultaneous process must have its own
temporary directory because Takumi extracts a native DLL there. Compare repeated,
alternating requests with the current deployment; report cold-start and warmed
latencies separately. Back up the executable, matching symbols and configuration,
stop databases gracefully through the installation's service manager, then replace
only the application binaries. After restarting, verify service readiness,
account/binding counts, configuration fingerprints and real command responses.
