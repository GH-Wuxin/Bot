Kanon latency changes

Player profiles are reused only within a single command and only when the player ID and the requested ruleset match. The API's preferred playmode is not used as a substitute for the requested statistics mode. Scores and PP calculations are not put behind a new persistent cache.

IAM and Kagami read requests are skipped when the corresponding integration has no configured key. The two integrations remain independently configurable.

Static images use URL-versioned cache entries. Validated entries are reused locally for five minutes, then refreshed in the background with at most two background downloads at a time. Failed refreshes retain the previous valid image and back off for 30 seconds. Changed URLs use a new entry immediately. Existing ETag caches are validated once before migration. Files contain the original downloaded bytes; image dimensions, encoding quality, templates, fonts, and PP/rendering dependencies are unchanged. Content-addressed image files keep existing render requests safe during updates.

Wuxin can opt into a completion extension on the existing OneBot WebSocket connection:

- Request header: `X-Wuxin-Completion: 1`.
- Message field: `wuxin_request_id`, a UUID in D format.
- After the complete message handler and all reply acknowledgements finish, Kanon sends a meta event with `post_type=meta_event`, `meta_event_type=wuxin_bridge`, `sub_type=command_complete`, and the same `wuxin_request_id`.
- Legacy clients do not receive this event. Wuxin accepts it only for the matching request after a valid reply. Legacy peers keep the normal settling/deadline behavior.

Offline data/cache contracts can be run with `dotnet run --project tools/latency-validation/LatencyValidation.csproj`. Set `KANON_TEST_DATA_DIR` to an isolated writable test directory if the host virtualizes its temporary directory. The runner uses mocked HTTP and does not load production configuration or access a business database.

Release validation compares fixed inputs against the existing deployed assembly, including std NM/HD/DT, taiko, catch, mania and the profile card. PP outputs must match exactly; image pixels must match except the profile card's existing wall-clock timestamp. A deployment using pinned published dependencies must retain those dependency bytes and verify the single-file package before activation.
