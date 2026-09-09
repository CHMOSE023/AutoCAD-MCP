# AutoCAD MCP end-to-end smoke test
# Prereq: AutoCAD has a DWG open AND the plugin is NETLOAD'ed (MCPSTATUS shows running).
# Usage:  powershell -File scripts\test-mcp.ps1  [-Endpoint http://127.0.0.1:7130/mcp]

param(
    [string]$Endpoint = "http://127.0.0.1:7130/mcp",
    [string]$OutDir = "D:\AutoCADMCP_DWG"
)

# Artifacts go to <OutDir>\Capture (png), <OutDir>\PFD (pdf), <OutDir> (dwg).
$CaptureDir = Join-Path $OutDir "Capture"
$PdfDir     = Join-Path $OutDir "PFD"
foreach ($d in @($OutDir, $CaptureDir, $PdfDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}

$ErrorActionPreference = "Stop"
$script:id = 0

function Invoke-Mcp {
    param([string]$Method, [hashtable]$Params)
    $script:id++
    $body = @{ jsonrpc = "2.0"; id = $script:id; method = $Method }
    if ($Params) { $body.params = $Params }
    $json = $body | ConvertTo-Json -Depth 10 -Compress
    # PowerShell 5.1's Invoke-RestMethod does not send body as UTF-8 by default; convert to bytes explicitly
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Uri $Endpoint -Method Post -ContentType "application/json; charset=utf-8" `
        -Headers @{ "Accept" = "application/json, text/event-stream"; "MCP-Protocol-Version" = "2025-06-18" } `
        -Body $bytes
}

function Call-Tool {
    param([string]$Name, [hashtable]$Arguments = @{})
    $r = Invoke-Mcp -Method "tools/call" -Params @{ name = $Name; arguments = $Arguments }
    $text = [string]$r.result.content[0].text
    $mark = if ($r.result.isError) { "ERR " } else { "ok  " }
    $flat = ($text -replace "`r?`n", " ")
    if ($flat.Length -gt 140) { $flat = $flat.Substring(0, 140) + "..." }
    Write-Host ("  {0}{1,-18} -> {2}" -f $mark, $Name, $flat)
    return $text
}

Write-Host "endpoint: $Endpoint`n"

Write-Host "== initialize =="
$init = Invoke-Mcp -Method "initialize" -Params @{
    protocolVersion = "2025-06-18"; capabilities = @{}; clientInfo = @{ name = "test-mcp.ps1"; version = "0" }
}
Write-Host ("  server: {0} v{1}, protocol {2}" -f `
    $init.result.serverInfo.name, $init.result.serverInfo.version, $init.result.protocolVersion)

Invoke-RestMethod -Uri $Endpoint -Method Post -ContentType "application/json" `
    -Headers @{ "Accept" = "application/json, text/event-stream" } `
    -Body '{"jsonrpc":"2.0","method":"notifications/initialized"}' | Out-Null

Write-Host "`n== tools/list =="
$tools = (Invoke-Mcp -Method "tools/list").result.tools
Write-Host ("  {0} tools: {1}" -f $tools.Count, (($tools | ForEach-Object { $_.name }) -join ", "))

Write-Host "`n== tools/call =="
$st = Call-Tool "get_status"
if ($st -match '"ready"\s*:\s*false') {
    Write-Host "`nNo drawing open in AutoCAD - skipping drawing tests. Open/new a DWG and retry."
    return
}
Call-Tool "list_layers"               | Out-Null
Call-Tool "create_layer"      @{ name = "MCP-TEST"; colorIndex = 1 } | Out-Null
Call-Tool "set_current_layer" @{ name = "MCP-TEST" }                 | Out-Null
$h1 = (Call-Tool "draw_line"  @{ x1 = 0; y1 = 0; x2 = 5000; y2 = 3000 }) -replace "handle=", ""
Call-Tool "draw_circle"       @{ cx = 2500; cy = 1500; r = 800 }     | Out-Null
Call-Tool "draw_polyline"     @{ points = @(@(0, 0), @(5000, 0), @(5000, 3000), @(0, 3000)); closed = $true } | Out-Null
Call-Tool "draw_text"         @{ x = 0; y = 3200; height = 300; content = "MCP TEST" } | Out-Null
Call-Tool "query_entities"    @{ type = "Line" }                     | Out-Null
if ($h1 -match '^[0-9A-Fa-f]+$') {
    Call-Tool "get_entity"        @{ handle = $h1 }                      | Out-Null
    Call-Tool "move"              @{ handle = $h1; dx = 0; dy = 500 }    | Out-Null
    Call-Tool "copy"              @{ handle = $h1; dx = 0; dy = 300; count = 2 } | Out-Null
}
Call-Tool "zoom_extents"                                             | Out-Null
if ($h1 -match '^[0-9A-Fa-f]+$') { Call-Tool "erase_entity" @{ handle = $h1 } | Out-Null }

Write-Host "`n== P1: capture_view =="
$cap = Invoke-Mcp -Method "tools/call" -Params @{ name = "capture_view"; arguments = @{ maxWidth = 1000 } }
$img = $cap.result.content | Where-Object { $_.type -eq "image" }
if ($img) {
    $bytes = [Convert]::FromBase64String($img.data)
    $out = Join-Path $CaptureDir "capture.png"
    [IO.File]::WriteAllBytes($out, $bytes)
    Write-Host ("  ok  capture_view       -> {0} bytes ({1}) saved to {2}" -f $bytes.Length, $img.mimeType, $out)
}
else {
    Write-Host "  ERR capture_view       -> no image in response"
}

Write-Host "`n== P1: save_as =="
$tmp = Join-Path $OutDir ("mcp-test-{0}.dwg" -f (Get-Date -Format "HHmmss"))
Call-Tool "save_as" @{ path = $tmp } | Out-Null

Write-Host "`n== eval_lisp =="
$probe = Call-Tool "eval_lisp" @{ code = '(+ 1 2 3)' }
if ($probe -match 'MCPLISP') {
    Write-Host "  eval_lisp disabled - run MCPLISP in AutoCAD to enable, then retry."
}
else {
    Call-Tool "eval_lisp" @{ code = '(getvar "DWGNAME")' }                                       | Out-Null
    Call-Tool "eval_lisp" @{ code = '(mapcar (function (lambda (x) (* x x))) (list 1 2 3 4 5))' } | Out-Null
    Call-Tool "eval_lisp" @{ code = '(defun mcp-selftest (n) (* n 10))' }                        | Out-Null
    Call-Tool "eval_lisp" @{ code = '(mcp-selftest 7)' }                                         | Out-Null   # expect 70 (defun persists across calls)
    Call-Tool "eval_lisp" @{ code = '(/ 1 0)' }                                                  | Out-Null   # expect a caught LISP error
}

Write-Host "`n== P2: draw =="
Call-Tool "draw_arc"     @{ cx = 8000; cy = 0; r = 800; startAngleDeg = 0; endAngleDeg = 120 } | Out-Null
Call-Tool "draw_ellipse" @{ cx = 8000; cy = 2000; majorX = 1000; majorY = 0; ratio = 0.5 }    | Out-Null
Call-Tool "draw_point"   @{ x = 8000; y = 3000 }                                               | Out-Null
$mt = (Call-Tool "draw_mtext" @{ x = 0; y = 6000; width = 4000; content = "P2 mtext line1\Pline2"; height = 300 }) -replace "handle=", ""

Write-Host "`n== P2: modify (native) =="
$rect = (Call-Tool "draw_polyline" @{ points = @(@(6000,8000), @(9000,8000), @(9000,11000), @(6000,11000)); closed = $true }) -replace "handle=", ""
if ($rect -match '^[0-9A-Fa-f]+$') { Call-Tool "explode" @{ handle = $rect } | Out-Null }
$bk = (Call-Tool "draw_line" @{ x1 = 0; y1 = 12000; x2 = 4000; y2 = 12000 }) -replace "handle=", ""
if ($bk -match '^[0-9A-Fa-f]+$') { Call-Tool "break_entity" @{ handle = $bk; x1 = 1000; y1 = 12000; x2 = 2500; y2 = 12000 } | Out-Null }
$j1 = (Call-Tool "draw_line" @{ x1 = 0; y1 = 13000; x2 = 2000; y2 = 13000 }) -replace "handle=", ""
$j2 = (Call-Tool "draw_line" @{ x1 = 2000; y1 = 13000; x2 = 5000; y2 = 13000 }) -replace "handle=", ""
if (($j1 -match '^[0-9A-Fa-f]+$') -and ($j2 -match '^[0-9A-Fa-f]+$')) { Call-Tool "join" @{ handles = @($j1, $j2) } | Out-Null }

Write-Host "`n== P2: modify (command queue) =="
$la = (Call-Tool "draw_line" @{ x1 = 0; y1 = 8000; x2 = 3000; y2 = 8000 }) -replace "handle=", ""
$lb = (Call-Tool "draw_line" @{ x1 = 3000; y1 = 8000; x2 = 3000; y2 = 11000 }) -replace "handle=", ""
if (($la -match '^[0-9A-Fa-f]+$') -and ($lb -match '^[0-9A-Fa-f]+$')) {
    Call-Tool "fillet" @{ handle1 = $la; handle2 = $lb; radius = 400 } | Out-Null
}
$cut = (Call-Tool "draw_line" @{ x1 = 1000; y1 = 6500; x2 = 1000; y2 = 7500 }) -replace "handle=", ""
$tg  = (Call-Tool "draw_line" @{ x1 = -500; y1 = 7000; x2 = 5000; y2 = 7000 }) -replace "handle=", ""
if (($cut -match '^[0-9A-Fa-f]+$') -and ($tg -match '^[0-9A-Fa-f]+$')) {
    Call-Tool "trim" @{ cutting = @($cut); targets = @($tg) } | Out-Null
}

Write-Host "`n== P2: dim / hatch =="
Call-Tool "dim_linear" @{ x1 = 0; y1 = 0; x2 = 5000; y2 = 0; dimX = 2500; dimY = -800 } | Out-Null
$hr = (Call-Tool "draw_polyline" @{ points = @(@(11000,0), @(14000,0), @(14000,2000), @(11000,2000)); closed = $true }) -replace "handle=", ""
if ($hr -match '^[0-9A-Fa-f]+$') {
    Call-Tool "hatch" @{ boundaryHandles = @($hr); pattern = "ANSI31"; scale = 50 } | Out-Null
}

Write-Host "`n== P2: select / measure =="
Call-Tool "select" @{ type = "Line" }                | Out-Null
Call-Tool "move"   @{ useSelection = $true; dx = 0; dy = 200 } | Out-Null   # expect all Lines shifted
if ($hr -match '^[0-9A-Fa-f]+$') {
    Call-Tool "measure_area" @{ handle = $hr }        | Out-Null   # expect area = 3000 * 2000
}
Call-Tool "measure_distance" @{ x1 = 0; y1 = 0; x2 = 3000; y2 = 4000 } | Out-Null   # expect distance 5000

Write-Host "`n== P3.5: linetypes / layer lineweight =="
Call-Tool "list_linetypes" | Out-Null
Call-Tool "create_layer" @{ name = "MCP-AXIS"; colorIndex = 4; linetype = "CENTER"; lineWeight = 0.13 } | Out-Null
Call-Tool "create_layer" @{ name = "MCP-WALL"; colorIndex = 2; lineWeight = 0.7 }                       | Out-Null
Call-Tool "create_layer" @{ name = "MCP-ROOM"; colorIndex = 3 }                                         | Out-Null
Call-Tool "list_layers" | Out-Null   # expect MCP-AXIS = CENTER / 0.13, MCP-WALL = 0.70

Write-Host "`n== P3.5: array_rect / array_polar =="
# a 400x400 square as the array seed (one column / one parking stall)
$seed = (Call-Tool "draw_polyline" @{ points = @(@(0,20000), @(400,20000), @(400,20400), @(0,20400)); closed = $true; layer = "MCP-WALL" }) -replace "handle=", ""
$plBefore = ((Call-Tool "query_entities" @{ type = "LWPOLYLINE" }) | ConvertFrom-Json).matched
if ($seed -match '^[0-9A-Fa-f]+$') {
    Call-Tool "array_rect" @{ handle = $seed; rows = 3; cols = 4; rowSpacing = 1000; colSpacing = 800 } | Out-Null
}
$plAfter = ((Call-Tool "query_entities" @{ type = "LWPOLYLINE" }) | ConvertFrom-Json).matched
Write-Host "  lwpolylines: before=$plBefore after=$plAfter (3x4 keeps the source, expect +11)"

$spoke = (Call-Tool "draw_line" @{ x1 = 15000; y1 = 25000; x2 = 16500; y2 = 25000 }) -replace "handle=", ""
if ($spoke -match '^[0-9A-Fa-f]+$') {
    Call-Tool "array_polar" @{ handle = $spoke; centerX = 15000; centerY = 25000; count = 8; rotateItems = $true } | Out-Null
}

Write-Host "`n== P3.5: set_entity_layer =="
# a line drawn on the current layer, then moved to MCP-AXIS - the "102 blocks on the wrong layer" fix
$stray = (Call-Tool "draw_line" @{ x1 = 0; y1 = 28000; x2 = 3000; y2 = 28000 }) -replace "handle=", ""
if ($stray -match '^[0-9A-Fa-f]+$') {
    Call-Tool "set_entity_layer" @{ handle = $stray; layer = "MCP-AXIS"; byLayerColor = $true } | Out-Null
    Call-Tool "get_entity" @{ handle = $stray } | Out-Null   # expect layer = MCP-AXIS
}

Write-Host "`n== P3.5: block layer + bboxFromBase =="
$bsrc = (Call-Tool "draw_polyline" @{ points = @(@(10000,20000), @(12500,20000), @(12500,25000), @(10000,25000)); closed = $true }) -replace "handle=", ""
if ($bsrc -match '^[0-9A-Fa-f]+$') {
    # base point sits on the middle of the bottom edge, so bboxFromBase should read x -1250..1250, y 0..5000
    Call-Tool "define_block" @{ name = "MCP-STALL"; entityHandles = @($bsrc); baseX = 11250; baseY = 20000; keepSource = $false } | Out-Null
    Call-Tool "list_blocks"  | Out-Null
    Call-Tool "insert_block" @{ name = "MCP-STALL"; x = 20000; y = 20000; layer = "MCP-WALL" } | Out-Null
    $ins = ((Call-Tool "query_entities" @{ type = "INSERT"; layer = "MCP-WALL" }) | ConvertFrom-Json).matched
    Write-Host "  block refs on MCP-WALL: $ins (expect >= 1; 0 means the layer arg was ignored)"
}

Write-Host "`n== P3.5: check_overlap / check_inside / check_adjacency =="
# rooms A|B share an edge; C is pushed 1000 into B on purpose, and sticks 2000 out of the outline
$rA = (Call-Tool "draw_polyline" @{ points = @(@(0,30000), @(4000,30000), @(4000,33000), @(0,33000));      closed = $true; layer = "MCP-ROOM" }) -replace "handle=", ""
$rB = (Call-Tool "draw_polyline" @{ points = @(@(4000,30000), @(8000,30000), @(8000,33000), @(4000,33000)); closed = $true; layer = "MCP-ROOM" }) -replace "handle=", ""
$rC = (Call-Tool "draw_polyline" @{ points = @(@(7000,30000), @(11000,30000), @(11000,33000), @(7000,33000)); closed = $true; layer = "MCP-ROOM" }) -replace "handle=", ""
# outline stays off MCP-ROOM so it is not part of the checked set
$outline = (Call-Tool "draw_polyline" @{ points = @(@(-500,29500), @(9000,29500), @(9000,33500), @(-500,33500)); closed = $true }) -replace "handle=", ""

Call-Tool "check_overlap" @{ layer = "MCP-ROOM" } | Out-Null   # expect FAIL: B x C overlap 1000 x 3000
if ($outline -match '^[0-9A-Fa-f]+$') {
    Call-Tool "check_inside" @{ layer = "MCP-ROOM"; boundary = $outline } | Out-Null   # expect FAIL: C out by 2000 in +X
}
if (($rA -match '^[0-9A-Fa-f]+$') -and ($rB -match '^[0-9A-Fa-f]+$')) {
    Call-Tool "check_adjacency" @{ handleA = $rA; handleB = $rB }             | Out-Null   # expect PASS (shared edge)
}
if (($rA -match '^[0-9A-Fa-f]+$') -and ($rC -match '^[0-9A-Fa-f]+$')) {
    Call-Tool "check_adjacency" @{ handleA = $rA; handleB = $rC; gap = 100 }  | Out-Null   # expect FAIL, actual gap 3000
}
if (($rB -match '^[0-9A-Fa-f]+$') -and ($rC -match '^[0-9A-Fa-f]+$')) {
    Call-Tool "check_adjacency" @{ handleA = $rB; handleB = $rC }             | Out-Null   # expect OVERLAP, not PASS
}

Write-Host "`n== P3.6: draw_spline =="
$spBefore = ((Call-Tool "query_entities" @{ type = "Spline" }) | ConvertFrom-Json).matched

# fit points (default): the curve passes through every point
$sp1 = (Call-Tool "draw_spline" @{ points = @(@(16000,4000), @(17000,5200), @(18000,3800), @(19000,5000), @(20000,4200)) }) -replace "handle=", ""
# fit points + closed -> isPeriodic ctor; do NOT repeat the first point at the end
$sp2 = (Call-Tool "draw_spline" @{ points = @(@(16000,7000), @(17500,8200), @(19000,7000), @(17500,6000)); closed = $true }) -replace "handle=", ""
# fit points + start/end tangents (all four components must be given together)
$sp3 = (Call-Tool "draw_spline" @{ points = @(@(21000,4000), @(22000,5200), @(23000,4000)); startTangentX = 1; startTangentY = 1; endTangentX = 1; endTangentY = -1 }) -replace "handle=", ""
# control points, open -> clamped knot vector; needs at least degree+1 points
$sp4 = (Call-Tool "draw_spline" @{ method = "cv"; points = @(@(16000,10000), @(17000,11500), @(18000,9500), @(19000,11000), @(20000,10000)); degree = 3 }) -replace "handle=", ""
# control points + closed -> first "degree" control points wrapped onto the end + uniform knots
$sp5 = (Call-Tool "draw_spline" @{ method = "cv"; points = @(@(21000,10000), @(23000,10000), @(23000,12000), @(21000,12000)); degree = 3; closed = $true }) -replace "handle=", ""

if ($sp2 -match '^[0-9A-Fa-f]+$') { Call-Tool "get_entity" @{ handle = $sp2 } | Out-Null }   # expect closed=true
if ($sp4 -match '^[0-9A-Fa-f]+$') { Call-Tool "get_entity" @{ handle = $sp4 } | Out-Null }

# A closed cv spline must span its whole control polygon (21000..23000 x 10000..12000).
# Getting this wrong is silent: a bad knot vector yields a valid but ~250-wide sliver, no error raised.
if ($sp5 -match '^[0-9A-Fa-f]+$') {
    $e5 = (Call-Tool "get_entity" @{ handle = $sp5 }) | ConvertFrom-Json
    if ($e5.bbox -match '^\s*(-?[\d.]+),(-?[\d.]+)\s*\.\.\s*(-?[\d.]+),(-?[\d.]+)') {
        $w = [double]$matches[3] - [double]$matches[1]
        $h = [double]$matches[4] - [double]$matches[2]
        $verdict = if (($w -ge 1500) -and ($h -ge 1500)) { "ok " } else { "ERR" }
        Write-Host ("  {0} cv+closed spans {1} x {2} (expect about 2000 x 2000; a few hundred means the knot vector collapsed)" -f $verdict, [math]::Round($w), [math]::Round($h))
    }
}

$spAfter = ((Call-Tool "query_entities" @{ type = "Spline" }) | ConvertFrom-Json).matched
Write-Host "  splines: before=$spBefore after=$spAfter (expect +5)"

Write-Host "  -- these four are expected to come back ERR --"
Call-Tool "draw_spline" @{ method = "cv"; points = @(@(0,0), @(100,100)); degree = 3 } | Out-Null   # cv needs degree+1 points
Call-Tool "draw_spline" @{ method = "spiral"; points = @(@(0,0), @(100,100)) }         | Out-Null   # method must be fit or cv
Call-Tool "draw_spline" @{ points = @(@(0,0), @(100,100)); startTangentX = 1 }         | Out-Null   # tangent components come in pairs
Call-Tool "draw_spline" @{ points = @(@(0,0), @(100,100)); degree = 99 }               | Out-Null   # degree out of 1..11

Call-Tool "zoom_extents" | Out-Null
$cap2 = Invoke-Mcp -Method "tools/call" -Params @{ name = "capture_view"; arguments = @{ maxWidth = 1400 } }
$img2 = $cap2.result.content | Where-Object { $_.type -eq "image" }
if ($img2) {
    [IO.File]::WriteAllBytes((Join-Path $CaptureDir "capture-p2.png"), [Convert]::FromBase64String($img2.data))
    Write-Host "  capture-p2.png written."
}

Write-Host "`n== P2.5: mark / rollback (no MCPLISP needed) =="
$c0 = ((Call-Tool "query_entities" @{ type = "Circle" }) | ConvertFrom-Json).matched
Call-Tool "mark" @{ label = "before-risky" } | Out-Null
Call-Tool "draw_circle" @{ cx = 20000; cy = 0; r = 500 } | Out-Null
Call-Tool "draw_circle" @{ cx = 21000; cy = 0; r = 500 } | Out-Null
Call-Tool "draw_circle" @{ cx = 22000; cy = 0; r = 500 } | Out-Null
$c1 = ((Call-Tool "query_entities" @{ type = "Circle" }) | ConvertFrom-Json).matched
Call-Tool "rollback" | Out-Null
# rollback goes through the AutoCAD command queue, which only runs when AutoCAD pumps messages.
# A fixed sleep is not enough when AutoCAD sits in the background - poll instead.
$c2 = $c1
foreach ($i in 1..15) {
    Start-Sleep -Seconds 1
    $c2 = ((Call-Tool "query_entities" @{ type = "Circle" }) | ConvertFrom-Json).matched
    if ($c2 -eq $c0) { break }
}
Write-Host ("  (rollback settled after {0}s)" -f $i)
Write-Host "  circles: start=$c0  after +3=$c1  after rollback=$c2  (expect c2 == c0)"

Write-Host "`n== P2.5: get_status (readOnly / authRequired / sessionBackedUp / marks) =="
Call-Tool "get_status" | Out-Null

Write-Host "`n== P3: sysvars / units =="
Call-Tool "get_sysvars" | Out-Null
Call-Tool "get_sysvars" @{ names = @("LTSCALE", "OSMODE", "NOSUCHVAR") } | Out-Null   # expect NOSUCHVAR in errors
Call-Tool "set_sysvar"  @{ name = "LTSCALE"; value = 1 } | Out-Null
Call-Tool "get_units"   | Out-Null
Call-Tool "convert_length" @{ value = 1000; from = "mm"; to = "m" } | Out-Null        # expect 1 m
Call-Tool "set_units"   @{ insunits = "mm"; luprec = 2 } | Out-Null

Write-Host "`n== P3: layouts / viewports =="
Call-Tool "list_layouts" | Out-Null
Call-Tool "create_layout" @{ name = "MCP-P3"; paperSize = "A3"; landscape = $true; setCurrent = $true } | Out-Null
$vpText = Call-Tool "add_viewport" @{ layout = "MCP-P3"; centerX = 210; centerY = 148; width = 380; height = 250; scale = 100; viewCenterX = 2500; viewCenterY = 1500 }
$vpH = ""
if ($vpText -match "handle=([0-9A-Fa-f]+)") { $vpH = $matches[1] }
Call-Tool "list_viewports" @{ layout = "MCP-P3" } | Out-Null
if ($vpH) { Call-Tool "set_viewport" @{ handle = $vpH; scale = 200; locked = $true } | Out-Null }
Call-Tool "list_layouts" @{ includeViewports = $true } | Out-Null

Write-Host "`n== P3: plot to PDF =="
Call-Tool "list_plot_devices" | Out-Null
$pdf = Join-Path $PdfDir ("mcp-plot-{0}.pdf" -f (Get-Date -Format "HHmmss"))
Call-Tool "plot_pdf" @{ layout = "MCP-P3"; output = $pdf; paperSize = "A3"; landscape = $true } | Out-Null
if (Test-Path $pdf) {
    Write-Host ("  ok  plot_pdf file     -> {0} ({1} KB)" -f $pdf, [math]::Round((Get-Item $pdf).Length / 1KB, 1))
}
else {
    Write-Host "  ERR plot_pdf file     -> not found: $pdf"
}

Write-Host "`n== P3: xrefs =="
Call-Tool "list_xrefs" | Out-Null
if (Test-Path $tmp) {
    # reuse the save_as copy from the P1 section as an xref source
    Call-Tool "attach_xref"  @{ path = $tmp; x = 30000; y = 0; scale = 1 } | Out-Null
    Call-Tool "list_xrefs"   | Out-Null
    Call-Tool "manage_xrefs" @{ op = "reload" } | Out-Null
    Call-Tool "manage_xrefs" @{ op = "detach" } | Out-Null
}
else {
    Write-Host "  skip xref tests - $tmp missing"
}

Write-Host "`n== P3: documents =="
$before = ((Call-Tool "list_documents") | ConvertFrom-Json).count
Call-Tool "new_document" | Out-Null
$after = ((Call-Tool "list_documents") | ConvertFrom-Json).count
Write-Host "  documents: before=$before after_new=$after"
if ($after -gt $before) {
    Call-Tool "close_document" @{ save = $false; force = $true } | Out-Null
    Call-Tool "activate_document" @{ index = 0 } | Out-Null
    $final = ((Call-Tool "list_documents") | ConvertFrom-Json).count
    Write-Host "  documents after close: $final (expect $before)"
}

Write-Host "`n== P3: capture drawing area =="
$cap3 = Invoke-Mcp -Method "tools/call" -Params @{ name = "capture_view"; arguments = @{ maxWidth = 1200; region = "drawing"; zoomExtents = $true } }
$img3 = $cap3.result.content | Where-Object { $_.type -eq "image" }
if ($img3) {
    [IO.File]::WriteAllBytes((Join-Path $CaptureDir "capture-p3.png"), [Convert]::FromBase64String($img3.data))
    Write-Host ("  ok  capture drawing   -> {0}" -f ($cap3.result.content | Where-Object { $_.type -eq "text" }).text)
}
else {
    Write-Host "  ERR capture_view      -> no image in response"
}

Write-Host "`n== P3: cleanup =="
Call-Tool "set_layout"    @{ name = "Model" }   | Out-Null
Call-Tool "delete_layout" @{ name = "MCP-P3" }  | Out-Null
Call-Tool "get_status"    | Out-Null

Write-Host "`nDone. In AutoCAD check P0/P1/P2/P3 output. capture.png / capture-p2.png / capture-p3.png / $tmp / $pdf on disk."
