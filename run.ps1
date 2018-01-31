$projectDir = $PSScriptRoot
$shadowSpawnPath = Join-Path $projectDir "bin\ShadowSpawn.exe"
$hdiffPath = Join-Path $projectDir "bin\hdiff.exe"
$appExe = "StarcounterShadowspanTest.exe"
$dbName = "scshadowtest"
$dbInfo = "{ ""DatabaseName"" : ""$dbName"" }"
$debugLogs = "$projectDir\debug_logs"
$logFile = "$projectDir\src\StarcounterShadowspanTest\bin\Debug\StarcounterShadowspanTest.log"
$logDoneFile = "$projectDir\src\StarcounterShadowspanTest\bin\Debug\StarcounterShadowspanTest.log.done"
$driveLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray()
$env:Path = "$env:StarcounterBin;$env:Path"
$dbBackupDir = Join-Path $projectDir "db_bup"
$robocopyPath = (Get-Command robocopy | select -First 1).Source

function GetDoneTimestamp() {    
    $file = gci $logDoneFile

    if ($file.Exists) {
        return $file.LastWriteTime
    }
    return [System.DateTime]::MinValue
}

function GetFreeDrive() {
    $drives  = [System.IO.Directory]::GetLogicalDrives() | % { $_.Substring(0,1) }
    #$drives = Get-PSDrive -PSProvider FileSystem | select -ExpandProperty Root | % { $_.Substring(0,1) }
    $drives = $drives -join ""    

    foreach($c in $driveLetters) {
        if ($drives.IndexOf($c) -eq -1) {
            return "$c" + ":"
        }
    }
    throw "No free drive letter, cancelling mapping of drive";
}

function Directory-Delete() {
    Param(
        [Parameter(Mandatory=$True,Position=1)]
        [string] $pathToDelete,
        [Parameter(Mandatory=$True,Position=2)]
        [int] $attempts
    )    
    $lastAttempt = $attempts - 1
    Write-Host -ForegroundColor Green "Deleting dir $dbDirPath"
    for ($i = 0; $i -le $attempts; $i++) {
        try {
            if (Test-Path $pathToDelete) {
                rmdir $pathToDelete -Recurse -Force               

                if (Test-Path $pathToDelete) {
                    throw "Not deleted"
                }
            }
            break;
        } catch {
            $ex = $_.Exception

            if ($i -eq $lastAttempt) {
                Write-Error $ex
                throw "Failed to delete: $pathToDelete"
            } else {                
                Write-Warning "[$i] '$pathToDelete' Delete failed, retrying soon`n$ex"
                Start-Sleep -Seconds 1
            }            
        }
    }
}

function Database-Delete() {
    Param(
        [Parameter(Mandatory=$True,Position=1)]
        $scSettings
    )    
    try {
        Write-Host -ForegroundColor Green "Deleting db $dbName, if exits"
        wget -Uri "http://localhost:$env:StarcounterServerPersonalPort/api/tasks/deletedatabase" -Body $dbInfo -Method Post -ErrorAction Ignore
    } catch {
    }
    # Make sure to delete all folders as well
    Start-Sleep -Seconds 1
    $personalDir = $scSettings.PersonalDirectory

    foreach ($dbDir in @($scSettings.ImageDirectory, $scSettings.DatabaseDirectory, "Databases\$dbName")) {

        if ($dbDir) {
            $dbDirPath = Path-Combine $personalDir $dbDir

            if ($dbDirPath -ne $personalDir -and (Test-Path $dbDirPath)) {                
                Directory-Delete $dbDirPath 10                
            }
        }
    }
}

function Database-Create() {    
    Write-Host -ForegroundColor Green "Creating db: $dbName"
    staradmin new db $dbName DefaultUserHttpPort=48123
}

function Database-Restore() {    
    Param(
        [Parameter(Mandatory=$True,Position=1)]
        $scSettings
    )
    Write-Host -ForegroundColor Green "Restoring db: $dbName"
    robocopy $dbBackupDir $scSettings.PersonalDirectory /S /E /DCOPY:DA /COPY:DAT /IS /R:5 /W:1 /NP 
}

function Database-Start() {    
    Param(
        [Parameter(Mandatory=$True,Position=1)]
        $scSettings
    )
    Write-Host -ForegroundColor Green "Starting db: $dbName"
    staradmin start db $dbName
}

function App-Run() {
    $doneTime = GetDoneTimestamp
    Write-Host -ForegroundColor Green "Starting: $appExe"

    # Executes the main before exiting
    star --database=$dbName $appExe

    # Check that we have a new done timestamp
    $doneTime2 = GetDoneTimestamp    

    if ($doneTime2 -gt $doneTime) {    
        Write-Host -ForegroundColor Green "Done state updated"        
    } else {       
        throw "Done state not updated"
    }    
}

function Get-RelativePath([string] $sub) {
    $currPath = (Get-Location).Path
    $relPath = $sub.Substring($currPath.Length + 1)    
    return $relPath.TrimStart('.', '\')
}

function GetStarcounterSettings() {
    Write-Host -ForegroundColor Green "Downloading settings"
    $json = wget -Uri "http://localhost:$env:StarcounterServerPersonalPort/api/admin/settings/database" | ConvertFrom-Json

    if (!$json) {
        throw "Failed parse database settings: $dbName"
    } else {
        $imgDir = $json.ImageDirectory

        if (!$imgDir) {
            throw "Invalid ImageDirectory: '$imgDir'"
        }
    }    
    $imgDir = $imgDir.Replace("[DatabaseName]", $dbName)    
    $dumpDir = $json.DumpDirectory.Replace("[DatabaseName]", $dbName)    
    $tempDir = $json.TempDirectory.Replace("[DatabaseName]", $dbName)    
    $transDir = $json.TransactionLogDirectory.Replace("[DatabaseName]", $dbName)      
    $personalDir = [System.IO.Path]::GetDirectoryName($imgDir)
    $personalDir = [System.IO.Path]::GetDirectoryName($personalDir)
    $json | Add-Member -MemberType NoteProperty  -Name PersonalDirectory -Value $personalDir      

    try {
        Push-Location $personalDir
        
        if (Test-Path "Databases") {
            Push-Location "Databases"
            $dbCfgFile = "$dbName.db.config"
            $dbConfig = gci $dbCfgFile -File -Recurse
            Pop-Location

            if ($dbConfig) {
                $dbConfig = $dbConfig | sort LastWriteTime | select -Last 1
                $dbDir = $dbConfig.Directory.FullName
                $dbConfig = Get-RelativePath $dbConfig.FullName
                $dbRel = Get-RelativePath $dbDir        
            }
            
        }
        $imgDir = Get-RelativePath $imgDir
        $dumpDir = Get-RelativePath $dumpDir
        $tempDir = Get-RelativePath $tempDir
        $transDir = Get-RelativePath $transDir
        $logRel = "logs"        
        $json | Add-Member -MemberType NoteProperty  -Name DatabaseDirectory -Value $dbRel      
        $json | Add-Member -MemberType NoteProperty  -Name ConfigFile -Value $dbConfig
        $json | Add-Member -MemberType NoteProperty  -Name LogsDir -Value $logRel      
        $json.ImageDirectory = $imgDir
        $json.DumpDirectory = $dumpDir
        $json.TempDirectory = $tempDir
        $json.TransactionLogDirectory = $transDir
    } finally {
        Pop-Location
    }

    return $json

}
function Path-Combine([string] $root, [string] $sub) {
    if ($root.EndsWith(':')) {
        $root = $root + "\"
    }
    if ($sub.StartsWith("\")) {
        $sub = $sub.TrimStart('\')
    }

    $combinedPath = [System.IO.Path]::Combine($root, $sub)
    return $combinedPath
}

function CopyScript-Create 
{
    Param(
        [Parameter(Mandatory=$True,Position=1)]
        $scSettings,
        [Parameter(Mandatory=$True,Position=2)]
        [string]$sourceDir,
        [Parameter(Mandatory=$True,Position=3)]
        [string]$mappedDrive,
        [Parameter(Mandatory=$True,Position=4)]
        [string]$targetDir
    )    
        
    $imgDir = $scSettings.ImageDirectory
    $personalDir = $scSettings.PersonalDirectory
    try {
        Push-Location $personalDir
        $dbRel = $scSettings.DatabaseDirectory        
        $imgRel = Resolve-Path $imgDir -Relative
        $logRel = "\logs"        
        $cmd = [System.IO.Path]::GetTempFileName() + ".cmd"
        Write-Host -ForegroundColor Green "[Copy-Script] $cmd, Personal: $personalDir"
        Set-Content $cmd -Value "@echo off`n@echo Backup of $dbName, will run hash diff on files as well`n"                
        Add-Content $cmd -Value "echo."
        $i = 0;

        foreach ($relPath in @($imgRel, $logRel, $dbRel)) {
            $rp = $relPath.TrimStart('.')
            $srcPath = Path-Combine $sourceDir $rp
            $shadowPath = Path-Combine $mappedDrive $rp
            $trgPath = Path-Combine $targetDir $rp

            # Diff the shadow drive with the real folder            
            $logFile = "$debugLogs\diff_src_shadow_$i.log"
            Add-Content $cmd -Value "`necho Logging diff to $logFile"
            Add-Content $cmd -Value """$hdiffPath"" ""$srcPath"" ""$shadowPath"" --debug --hash --RedirectErrors -logfile=$logFile"
            Add-Content $cmd -Value "echo."

            # Copy from shadow drive
            $logFile = "$debugLogs\robocopy_$i.log"
            Add-Content $cmd -Value "`necho Robocopy ""$shadowPath"" ""$trgPath"", logging to $logFile"
            Add-Content $cmd -Value """$robocopyPath"" ""$shadowPath"" ""$trgPath"" /MIR /IS /FFT /Z /NDL /NP /R:5 /W:1 /LOG:$logFile /TEE"
            Add-Content $cmd -Value "echo."

            # Diff the result 
            $logFile = "$debugLogs\diff_shadow_backup_$i.log"
            Add-Content $cmd -Value "`necho Logging diff to $logFile"
            Add-Content $cmd -Value """$hdiffPath"" ""$shadowPath"" ""$trgPath"" --debug --hash --RedirectErrors -logfile=$logFile"
            Add-Content $cmd -Value "echo."
            $i++
        }
        return $cmd
    } finally {
        Pop-Location        
    }
}

function ShadowSpawn {
    Param(
        [Parameter(Mandatory=$True,Position=1)]
        [string]$sp_sourceDir,
        [Parameter(Mandatory=$True,Position=2)]
        [string]$sp_mappedDrive,
        [Parameter(Mandatory=$True,Position=3)]
        [string]$sp_command
    )
    $errorAction = $ErrorActionPreference
    $ErrorActionPreference = "Ignore"
    try {
        $sp_exe = gci $shadowSpawnPath
        $sp_dir = $sp_exe.Parent.FullName
        Push-Location $sp_dir
        Write-Host -ForegroundColor Green "[Shadowspawn] $sp_sourceDir => $sp_mappedDrive, Cmd: $sp_command"
        &$sp_exe $sp_sourceDir $sp_mappedDrive $sp_command
    } finally {
        $ErrorActionPreference = $errorAction
        Pop-Location
    }

}


function MainLoop() {
    Clear


    # Clean up left over drives from shadowspawn
    Get-PSDrive -PSProvider FileSystem | ? { !$_.Free  } | Remove-PSDrive

    #Delete debug logs

    if (Test-Path $debugLogs) {
        Push-Location $debugLogs
        try {
            gci | rm -Force -ErrorAction Ignore
            rm $debugLogs -Force -Recurse -ErrorAction Ignore
        } catch {
        } finally {
            Pop-Location
        }
    
    }


    # Build, needs roslyn
    Push-Location "$projectDir\src\StarcounterShadowspanTest"
    $msb15="${env:ProgramFiles(x86)}\Microsoft Visual Studio\2017\Enterprise\MSBuild\15.0\bin"

    if (Test-Path $msb15) {
        $env:Path = "$msb15;$env:Path"
        $env:MSB_TV=15.0
        $env:VisualStudioVersion=15.0
    } else {
        Write-Warning "Failed to locate msbuild 15, you probably need to build manually"
    }

    Write-Host -ForegroundColor Green "Compiling"
    msbuild "/p:configuration=debug;platform=x64"
    Push-Location bin\debug


    # Delete database if it exists
    $dbSettings = GetStarcounterSettings
    Database-Delete $dbSettings

    # Run the database with custom port
    Database-Create
    $dbSettings = GetStarcounterSettings

    # Run the app, this is the first time and should create data
    App-Run

    # Create a bat file for the files to copy
    $drive = GetFreeDrive
    $srcDir = $dbSettings.PersonalDirectory
    Write-Host -ForegroundColor Green "FreeDrive: $drive"
    $cmdFile = CopyScript-Create $dbSettings $srcDir $drive $dbBackupDir

    # Now create a backup using shadowspawn with the app running
    Shadowspawn $srcDir $drive $cmdFile

    Database-Delete $dbSettings

    Database-Restore $dbSettings
    $dbSettings = GetStarcounterSettings

    # Call start on the database, this should run the restored db
    Database-Start $dbSettings

    App-Run
}

$ErrorActionPreference = "Stop"

try {
    MainLoop
} catch {
    $ex = $_.Exception
    
    while ($ex) {
        Write-Host -ForegroundColor Red "$ex"

        $ex = $ex.InnerException
    }
}