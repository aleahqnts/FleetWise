# CameraCount Mobile

RouteSync's camera-based passenger counter. Native Kotlin Android app for the bus
dashboard phone. Counts boarding passengers (line-cross past the pay zone) and writes
`trips.total_boarded` + `trips.count_heartbeat` to the shared Supabase DB every 5s.

Full spec + phase plan: [`../CameraApp-plan.md`](../CameraApp-plan.md).

## Open / build

Open this folder (`CameraCountMobile/`) in **Android Studio** — it will configure Gradle
on first sync (no wrapper committed). Min SDK 26, Kotlin + Jetpack Compose.

## Status

- **Phase 0 (this):** scaffold + Supabase REST client (`data/SupabaseApi.kt`) + in-app
  "Test DB connection" smoke check.
- Phase 1: vehicle bind + 3–5s trip poll + fake `+1` counter proving the DB bridge.
- Phase 3+: CameraX → YOLO11n (LiteRT) → ByteTrack → line-cross counting.
