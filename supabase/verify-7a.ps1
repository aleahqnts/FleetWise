# Phase 7a verify: mint device JWT + prove PostgREST accepts it (role -> app_camera).
# Run:  powershell -File C:\Users\Chester\FleetWise\supabase\verify-7a.ps1
$base = "https://vrtluruqaxutecydbrsq.supabase.co"
$key  = "sb_publishable_sjkjW2K7QOPRKmixJdhSgA_8rPtoFzD"

$sec = Read-Host "Fleet bind secret" -AsSecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))

# 1) mint app_camera JWT
$tok = $null
try {
    $body = @{ device_id = "cam-00000000"; fleet_secret = $plain } | ConvertTo-Json
    $r1 = Invoke-RestMethod -Method Post -Uri "$base/functions/v1/device-token" -ContentType "application/json" -Body $body
    $tok = $r1.token
    Write-Host "1) device-token mint: PASS" -ForegroundColor Green
} catch {
    Write-Host "1) device-token mint: FAIL - wrong secret or fn error" -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}

$hdr = @{ apikey = $key; Authorization = "Bearer $tok" }

# 2) JWT-authed PostgREST read (role claim must map to app_camera + grants)
try {
    $url2 = "$base/rest/v1/vehicles?select=vehicle_id" + "&" + "limit=1"
    $rows = Invoke-RestMethod -Uri $url2 -Headers $hdr
    Write-Host "2) PostgREST accepts app_camera JWT: PASS" -ForegroundColor Green
} catch {
    Write-Host "2) PostgREST JWT read: FAIL - JWT_SECRET mismatch, or roles not granted to authenticator" -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
}

# 3) column guard: app_camera must NOT touch trip_status
try {
    $hdr3 = @{ apikey = $key; Authorization = "Bearer $tok"; Prefer = "return=minimal" }
    Invoke-RestMethod -Method Patch -Uri "$base/rest/v1/trips?trip_id=eq.__none__" -Headers $hdr3 -ContentType "application/json" -Body '{"trip_status":"Hacked"}'
    Write-Host "3) trip_status column guard: FAIL - PATCH accepted, grants too wide" -ForegroundColor Red
    exit 1
} catch {
    Write-Host "3) trip_status column guard: PASS (denied as expected)" -ForegroundColor Green
}

Write-Host ""
Write-Host "All 7a checks green." -ForegroundColor Green
