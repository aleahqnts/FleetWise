# CameraCount Mobile

RouteSync's camera-based passenger counter. Native Kotlin Android app for the bus
dashboard phone. Counts boarding passengers (line-cross past the pay zone) and writes
`trips.total_boarded` + `trips.count_heartbeat` to the shared Supabase DB every 5s.

Full spec + phase plan: [`../CameraApp-plan.md`](../CameraApp-plan.md).

## Open / build

Open this folder (`CameraCountMobile/`) in **Android Studio** — it will configure Gradle
on first sync (no wrapper committed). Min SDK 26, Kotlin + Jetpack Compose.

## Status

- **Phase 0 — done:** scaffold + Supabase REST client (`data/SupabaseApi.kt`); DB
  `count_heartbeat` column live, curl acceptance passed.
- **Phase 1 — code done, untested on device:** vehicle bind + passcode (`data/Prefs.kt`),
  4s trip poll + 5s count/heartbeat flush (`CounterViewModel.kt`), Setup/Waiting/Counting
  UI with fake `+1` button (`MainActivity.kt`).
- Phase 3+: CameraX → YOLO11n (LiteRT) → ByteTrack → line-cross counting.
