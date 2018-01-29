$scriptsDir = $PSScriptRoot
$appExe = "StarcounterShadowspanTest.exe"
$dbName = "scshadowtest"
$dbInfo = "{ ""DatabaseName"" : ""$dbName"" }"


# Build, needs roslyn
Push-Location "$scriptsDir\src\StarcounterShadowspanTest"
$msb15="${env:ProgramFiles(x86)}\Microsoft Visual Studio\2017\Enterprise\MSBuild\15.0\bin"

if (Test-Path $msb15) {
    $env:Path = "$msb15;$env:Path"
    $env:MSB_TV=15.0
    $env:VisuaVisualStudioVersion=15.0
} else {
    Write-Warning "Failed to locate msbuild 15, you probably need to build manually"
}

Write-Host "Compiling"
msbuild "/p:configuration=debug;platform=x64"

# Delete database if it exists
Write-Host "Deleting db $dbName, if exits"
wget -Uri "http://localhost:$env:StarcounterServerPersonalPort/api/tasks/deletedatabase" -Body $dbInfo -Method Post -ErrorAction Ignore

# Run the database with custom port
Write-Host "Creating db: $dbName"
Push-Location bin\debug
$env:Path = "$env:StarcounterBin;$env:Path"
staradmin new db $dbName DefaultUserHttpPort=48123

Write-Host "Starting: $appExe"
star --database=$dbName $appExe


