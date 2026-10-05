# DEV002 frozen offline contract (English derivative)

Status: planned; this contract does not activate implementation. Its inputs are registered read-only contracts such as domain-model, especially episode decimal semantics in §6.2. No old source, tests or acceptance are imported. The concrete bounds, receipt and preview rules below are this card's explicit engineering definitions, not new external production policy. Translation status at publication: draft, pending actual matching semantic-review evidence.

## 1. Identity, model and input

- WorkId, NumberingSystemId, EpisodeId, CoverageId and RequestId are nonempty GUIDs; titles or ordinals do not determine identity
- WorkKind is only Movie/Series. Work title is nonempty/non-whitespace, at most 512 characters, and preserved as supplied. Optional episode title allows null/empty; non-null values are at most 512 characters. No description field is added
- A Series has exactly one explicit manual NumberingSystemId at creation; a Movie has no system, episodes or coverage. WorkId, WorkKind and the Series system ID are immutable. Database relations must also reject Movie systems and cross-work/system references; table count is not frozen
- SeasonNumber ranges from 0 to Int32.MaxValue. Zero may be entered manually; never infer seasons from OVA/SP or external policy
- ExactDecimalOrdinal is only for episodes: ASCII [0-9]+(\.[0-9]+)?, at most 28 integer and 28 fractional digits, at most 57 raw characters. Preserve raw spelling; remove integer leading zeros and fractional trailing zeros, canonicalizing all-zero values to 0. 12.500 and 12.5 share a canonical key. Explicitly reject signs, exponents, locale separators, whitespace and excess length. No implicit float/REAL/native-decimal rounding. Compare exact numeric values, not SQLite REAL or ordinary lexical order
- EpisodeKey=(SeasonNumber,ExactDecimalOrdinal); EpisodeRecord carries its ID, system, key and optional title. The unique key is (WorkId,NumberingSystemId,SeasonNumber,CanonicalEpisodeOrdinal)
- CoverageRecord carries its ID, system and explicit ordered EpisodeId members. An existing coverage must have nonempty, nonduplicate members all in the same work/system. Discontinuous and cross-season members are valid; never fill intervening items. Reordering does not change CoverageId or the member set, while display order round-trips. Database constraints include unique (coverage,episode) and (coverage,ordinal), plus composite foreign keys
- WorkSnapshot contains work identity/type/title, manual system (null for Movie), revision, episodes and coverage. Caller mutation of input collections must not mutate snapshots or returned receipts

- Explicitly reject null Episodes, Coverage, Assignments or Members collections, null elements and missing required keys. Never reinterpret a null replacement as an allowed empty catalog and clear existing data. Only Episode.Title and Movie.SystemId retain their specified null exceptions

## 2. Commands, queries and transactions

- CreateWork(RequestId,WorkId,Kind,Title,NumberingSystemId?) succeeds at revision 1; same-title works with distinct WorkIds are valid
- ReplaceManualCatalog(RequestId,WorkId,ExpectedRevision,NumberingSystemId?,Title,Episodes,Coverage) replaces only that work's mutable aggregate. The explicit system must match the immutable work system (null for Movie), including for empty catalogs. Empty catalogs are allowed; any existing coverage still needs nonempty members. Each successful new mutation increments revision exactly once. Explicitly reject growth beyond long.MaxValue, without REAL conversion or wraparound
- Get/List consistently read the database. A WorkSnapshot's revision/root/children come from one read transaction. Ordering is deterministic: episodes use exact season/episode numeric order; coverage preserves explicit member order
- Complete format/normalization validation and freeze the command first; fingerprinting and mutation use the same immutable representation. Each write uses a short transaction: look up an existing RequestId and replay/conflict first; only new requests then validate current references, CAS against ExpectedRevision and commit state plus successful receipt together. At most one new write wins real two-connection competition for the same Work and ExpectedRevision. BUSY/LOCKED is a distinct storage failure, never a fabricated revision conflict or success
- RequestId is scoped to the whole isolated Catalog database. The canonical fingerprint covers every command field: command kind, WorkId, ExpectedRevision where applicable, Kind only for Create, Work Title, NumberingSystemId including episode/coverage ownership, optional episode title with null distinct from empty, canonical keys, entity IDs, members and display order. Sort episode/coverage record lists canonically by ID while retaining coverage member order. Raw episode spelling is the only omission; do not supplement the fingerprint with current database Kind, title, revision or other mutable values
- The same token and canonical request replay the first persisted complete receipt/snapshot/revision and raw spelling, even after the work changes later. Thus 12.500 and 12.5 can replay. The same token with a different canonical request conflicts with zero changes. A new token may explicitly change spelling and normally increment revision
- A fully rolled-back failed command consumes no token and stores no failed-acceptance state. No general Sqlite exception can count as idempotent success; failures leave neither partial aggregates nor receipts
- The only database creation entry is OfflineCatalogDatabase.CreateFresh(), creating its own unique new temporary directory/file. It accepts no external path or connection string. OpenSession() uses independent real connections with Pooling=false and foreign keys enabled. Use only new databases, never old/real media paths. Closing resources must not silently discard still-needed test evidence. Inject test-only faults only in a new test database; add no product fault hook

## 3. Pure numbering preview

ManualNumberingPreview.Build(snapshot,request); the request contains WorkId, NumberingSystemId, ExpectedRevision and at least one ordered (SourceEpisodeId,TargetEpisodeKey) assignment. Only Series is supported. All three bindings must match the supplied snapshot; source IDs must exist in the same work/system. Reject duplicate sources, duplicate canonical targets and targets colliding with existing unmapped episodes. Vacate all mapped source keys conceptually before validation, allowing complete swaps.

Return each original key, explicit manual target and the three bindings/clear issues, preserving caller order. No filename parsing, cumulative inference, ambiguous-range expansion or repository dependency; do not mutate snapshot/database/receipt/revision. No claim that the supplied snapshot is still live-current and no adoption endpoint.

## 4. Eight risk groups and completion

1. Creation without external IDs; distinct same-title identity; empty GUID/invalid kind rejection; Movie cannot own systems/episodes/coverage
2. Raw/canonical keys, zero, decimal equivalence, exact ordering, large precision and explicit boundary rejection without truncation, rounding or locale dependence
3. Cross-season/discontinuous round-trips; order changes do not turn identity/sets into ranges; duplicate or empty coverage members reject
4. Service and real database constraints reject cross-work/system references, duplicate canonical keys and duplicate members/ordinals; no fixed table count
5. Stale revision refusal, real concurrent mutation entry on two SQLite connections with one commit, consistent snapshots without mixed revisions, accurate BUSY/LOCKED classification
6. Same canonical replay and different-payload conflict; original receipts stay faithful after later revisions; a new-database SQL trigger witnesses the new revision/child rows before actually failing receipt insertion, proving full rollback and an unconsumed token without product fault hooks
7. Preview work/system/revision binding, source/target conflicts and legal swaps; unchanged snapshot/database/receipt/revision; no live-current claim from in-memory matching
8. Locked restore/build/nonempty fresh tests and the existing inert-host check; host remains root/health only, with no Catalog production route, extra listener, worker, real media/old database/external integration or credential side effect

Necessary results bind this card's exact candidate, actual commands/exits and genuine independent review. Write new unit/SQLite integration tests from scratch. The previous engineering foundation is a regression dependency, not new Catalog or complete UI/HTTP/MCP acceptance. Close Chinese/English changes with independent semantic review bound to actual Git bytes; unreviewed items remain draft/not-run.
