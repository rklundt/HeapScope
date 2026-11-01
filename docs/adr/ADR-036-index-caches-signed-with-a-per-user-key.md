# ADR-036 Index caches signed with a per-user key (added in sprint 3.7)

- Context: the peer review of 3.5.0 (P14) found that an index cache next to its dump is preferred and trusted on unkeyed
  checksums and a fingerprint of size, time and the first and last MiB. A dump's time survives unzipping, so a shipped
  `app.dmp.hsidx` decided every count, size and root HeapScope showed.
- Decision: the worker loads this user's key at startup (`IndexCacheKey.UseForThisUser`: `index-cache.key`, 32 random
  bytes, owner-only, in HeapScope's data folder, created on first use under a lock file so processes starting together
  share one key). The writer signs the header region (header and
  section directory, which carries every section's checksum) with HMAC-SHA256 into the region's last 32 bytes, past the
  largest possible directory, so the format does not change; base caches and sidecars alike. The reader verifies it after
  the header checksum; a mismatch is `IndexCacheInvalidReason.Untrusted`, treated like any invalid cache: the dump is read
  again and the cache rewritten. Without a key (tests, tools) nothing is signed or checked; tests use a scoped key. The
  worker loads the key once its log exists and logs where it came from; when the key file can be neither read nor
  created (a folder of another user, a full disk), that run signs with a random key of its own and logs a Warning,
  never running without a key (3.7 review H4).
- Consequences: every cache written before 3.7 is rebuilt once (its note says it was unsigned). A cache moved between
  users or machines is rebuilt. Deleting the key file makes every cache rebuild; so does a run with a key of its own, for
  the caches it wrote, and a worker restarted during such a run refuses to reopen its snapshots automatically (the log
  shows why). The key protects against files that arrive with a dump, not against a process of the same user, which can
  read the key. The signature covers the header region, which carries each section's XxHash3: a section's integrity rests
  on that unkeyed hash, so someone who can write into the cache's folder after the user's HeapScope signed a cache there
  could in principle substitute colliding section contents; that is outside the threat model (files that arrive with a
  dump), and keyed section digests are the step if it widens.
- Amended in sprint 3.8 (3.7 review M11): the key is no longer ambient. Every index cache reader and writer takes an
  `IndexCacheSigner` explicitly: the worker's comes from `IndexCacheSigner.ForThisUser` (its DI container loads it once
  the log exists and logs the outcome), and tools and tests that read only caches they wrote pass `IndexCacheSigner.None`
  written out, which a rule test keeps out of the worker. The key file lives in HeapScope's data folder
  (`HeapScopeFolders`), which `--data-folder` or `HEAPSCOPE_DATA_FOLDER` moves; a worker with another data folder has
  another key, so it rebuilds caches the first one wrote (`SnapshotTests.A_cache_signed_by_another_users_key_is_rebuilt_and_noted`).
