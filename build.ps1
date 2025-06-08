function Is-Admin() {
    $currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function main() {
    if (-not (Is-Admin)) {
        Write-Host "error: administrator privileges required"
        return 1
    }

    if (Test-Path ".\build\") {
        Remove-Item -Path ".\build\" -Recurse -Force
    }

    mkdir ".\build\"

    # create folder structure
    mkdir ".\build\xtw\"

    # build application
    MSBuild.exe ".\xtw.sln" -p:Configuration=Release -p:Platform=x64

    # create final package
    Copy-Item ".\xtw\bin\x64\Release\*" ".\build\xtw\" -Recurse
    Copy-Item ".\xtw_etl_collection.bat" ".\build\xtw\"
    Copy-Item ".\PresentMon.exe" ".\build\xtw\"

    return 0
}

$_exitCode = main
Write-Host # new line
exit $_exitCode
