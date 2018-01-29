$scriptsDir = $PSScriptRoot
$shadowSpawn = Join-Path $PSScriptRoot "bin\ShadowSpawn.exe"
$appExe = "StarcounterShadowspanTest.exe"
$dbName = "scshadowtest"
$dbInfo = "{ ""DatabaseName"" : ""$dbName"" }"
$logFile = "$scriptsDir\src\StarcounterShadowspanTest\bin\Debug\StarcounterShadowspanTest.log"
$logDoneFile = "$scriptsDir\src\StarcounterShadowspanTest\bin\Debug\StarcounterShadowspanTest.log.done"
$driveLetters = "abcdefghijklmnopqrstuvwxyz".ToCharArray()
$env:Path = "$env:StarcounterBin;$env:Path"

function GetDoneTimestamp() {    
    $file = gci $logDoneFile

    if ($file.Exists) {
        return $file.LastWriteTime
    }
    return [System.DateTime]::MinValue
}

function GetFreeDrive() {
    $drives = Get-PSDrive -PSProvider FileSystem | select -ExpandProperty Root | % { $_.Substring(0,1) }
    $drives = $drives -join ""
    Write-Host $drives

    foreach($c in $driveLetters) {
        if (!($drives -contains $c)) {
            return $c
        }
    }
    throw "No free drive letter, cancelling mapping of drive";
}

function Database-Delete() {    
    Write-Host "Deleting db $dbName, if exits"
    wget -Uri "http://localhost:$env:StarcounterServerPersonalPort/api/tasks/deletedatabase" -Body $dbInfo -Method Post -ErrorAction Ignore
}

function Database-Create() {    
    staradmin new db $dbName DefaultUserHttpPort=48123
}

function App-Run() {
    $doneTime = GetDoneTimestamp
    Write-Host "Starting: $appExe"

    # Executes the main before exiting
    star --database=$dbName $appExe

    # Check that we have a new done timestamp
    $doneTime2 = GetDoneTimestamp    

    if ($doneTime2 -gt $doneTime) {    
        Write-Host "Done state updated"        
    } else {       
        throw "Done state not updated"
    }    
}

$ErrorActionPreference = "Stop"


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
Push-Location bin\debug

# Delete database if it exists
Database-Delete

# Run the database with custom port
Write-Host "Creating db: $dbName"
Database-Create


# Run the app, this is the first time and should create data
App-Run

# Now create a backup using shadowspawn
$drive = GetFreeDrive
Write-Host "FreeDrive: $drive"
