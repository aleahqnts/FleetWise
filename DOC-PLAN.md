# Documentation pass

Goal: the source reads as a codebase that was documented, not one that was narrated
while being built. Rules live in `.claude/skills/document-code/SKILL.md`. Invoke with
`/document-code` and name the batch.

## Why this is needed

Comments currently carry build history that means nothing to a reader: phase numbers
from the build order, references to planning documents that are not in the repository,
first-person narration, and post-mortems of bugs that no longer exist.

Measured, excluding `bin/`, `obj/`, `build/`:

| App | Files | Lines | Comment lines | Phase refs | First person | Em-dashes |
|---|---|---|---|---|---|---|
| CameraCountMobile | 15 | 3,349 | 652 (19%) | 40 | 27 | 59 |
| RouteSyncMobile | 55 | 5,060 | 434 (9%) | 16 | 10 | 48 |
| RouteSyncWeb | 93 | 20,932 | 1,404 (7%) | 13 | 17 | 185 |

The camera app is the worst by density: nearly one line in five is a comment, and it
holds most of the phase references.

## Order

Camera app first. It is small enough to finish in two sittings, and it is the densest,
so it calibrates the rules before they are applied to 93 web files.

Then the driver app: more files, but the comments are thin and mostly already factual.

Web dashboard last, split by area, because it is two thirds of the total.

## Phases

Each phase is one batch: read, edit, build, report. Stop after each so the diff stays
reviewable and nothing runs unattended.

### Camera app

- [ ] **C1** `ui/CameraScreen.kt` (163 comment lines, 10 phase refs). The largest file
      in the suite by comment volume. Do it alone.
- [ ] **C2** `CounterViewModel.kt`, `data/SupabaseApi.kt` (192 lines, 14 refs)
- [ ] **C3** `MainActivity.kt`, `data/Prefs.kt`, `camera/DetectorAnalyzer.kt`
- [ ] **C4** `camera/PersonTracker.kt`, `camera/YoloDetector.kt`, `camera/LensPicker.kt`,
      `camera/SnapshotCapture.kt`
- [ ] **C5** `WatcherService.kt`, `CountingService.kt`, `BootReceiver.kt`,
      `ui/RsIcons.kt`, `ui/Theme.kt`

### Driver app

- [ ] **D1** `Services/DriverDataService.cs`, `Services/BackNavigation.cs`
- [ ] **D2** `Components/Pages/TripActive.razor`, `Components/Pages/CameraCalibrate.razor`
- [ ] **D3** remaining `Services/` (`MessageWatch`, `TelemetryQueue`, `SessionService`,
      `AuthService`, `PhTime`, `AuthApi`)
- [ ] **D4** remaining `Components/Pages/` and `Platforms/Android/`

### Web dashboard

- [ ] **W1** `Controllers/DispatchController.cs` (125 lines). Alone.
- [ ] **W2** `Controllers/VehiclesController.cs` (107 lines). Alone.
- [ ] **W3** `Controllers/ReportsController.cs`, `Controllers/DashboardController.cs`,
      `Controllers/FleetMapController.cs`
- [ ] **W4** `Controllers/ScheduleController.cs`, `Controllers/UsersController.cs`,
      `Controllers/HomeController.cs`, `Controllers/AuditController.cs`
- [ ] **W5** `Services/` (`TelemetrySimulator`, `AuditLog`, `PhClock`,
      `RequirePermissionAttribute`, and the rest)
- [ ] **W6** `Program.cs`, `Models/`
- [ ] **W7** `Views/Reports`, `Views/Dispatch`
- [ ] **W8** `Views/Dashboard`, `Views/Vehicles`, `Views/Audit`, `Views/Schedule`
- [ ] **W9** remaining views

## Rules of engagement

- Comments only. No behaviour changes, no reformatting, no moved code.
- Build after every phase. Comment edits break string literals and Razor blocks more
  often than expected.
- Keep the fact, drop the story. Several comments record findings that cost real
  debugging time (the camera watchdog thresholds, the append-only trigger behaviour,
  the TFLite device allowlist). Those constraints stay, in one present-tense line.
- Anything that turns out to be a genuine bug gets reported, not silently fixed.

## Do not touch

`bin/`, `obj/`, `build/`, EF migrations, `wwwroot/lib/`, and the `supabase/*.sql` files
(the SQL comments are the schema's own documentation and stay as they are).
