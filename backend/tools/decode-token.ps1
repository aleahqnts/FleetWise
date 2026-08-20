# Decode the minted device JWT (header + payload only, no signature check).
$base = "https://vrtluruqaxutecydbrsq.supabase.co"
$sec = Read-Host "Fleet bind secret" -AsSecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))

$body = @{ device_id = "cam-00000000"; fleet_secret = $plain } | ConvertTo-Json
$tok = (Invoke-RestMethod -Method Post -Uri "$base/functions/v1/device-token" -ContentType "application/json" -Body $body).token

function Decode-Part($p) {
    $p = $p.Replace('-', '+').Replace('_', '/')
    switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p))
}
$parts = $tok.Split('.')
Write-Host "HEADER : $(Decode-Part $parts[0])"
Write-Host "PAYLOAD: $(Decode-Part $parts[1])"
