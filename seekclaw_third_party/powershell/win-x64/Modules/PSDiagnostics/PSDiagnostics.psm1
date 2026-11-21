# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
  PowerShell Diagnostics Module
  This module contains a set of wrapper scripts that
  enable a user to use ETW tracing in PowerShell 7.
 #>

$script:windir = [System.Environment]::GetEnvironmentVariable("windir", [System.EnvironmentVariableTarget]::Machine)

$script:Logman = "${script:windir}\system32\logman.exe"
$script:wsmanlogfile = "${script:windir}\system32\wsmtraces.log"
$script:wsmprovfile = "${script:windir}\system32\wsmtraceproviders.txt"
$script:wsmsession = "wsmlog"
$script:pssession = "PSTrace"
$script:psprovidername = "PowerShellCore"
$script:wsmprovidername = "Microsoft-Windows-WinRM"
$script:oplog = "/Operational"
$script:analyticlog = "/Analytic"
$script:debuglog = "/Debug"
$script:wevtutil = "${script:windir}\system32\wevtutil.exe"
$script:slparam = "sl"
$script:glparam = "gl"

function Start-Trace
{
    Param(
    [Parameter(Mandatory=$true,
               Position=0)]
    [string]
    $SessionName,
    [Parameter(Position=1)]
    [ValidateNotNullOrEmpty()]
    [string]
    $OutputFilePath,
    [Parameter(Position=2)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ProviderFilePath,
    [Parameter()]
    [Switch]
    $ETS,
    [Parameter()]
    [ValidateSet("bin", "bincirc", "csv", "tsv", "sql")]
    $Format,
    [Parameter()]
    [int]
    $MinBuffers=0,
    [Parameter()]
    [int]
    $MaxBuffers=256,
    [Parameter()]
    [int]
    $BufferSizeInKB = 0,
    [Parameter()]
    [int]
    $MaxLogFileSizeInMB=0
    )

    Process
    {
        $executestring = " start $SessionName"

        if ($ETS)
        {
            $executestring += " -ets"
        }

        if ($null -ne $OutputFilePath)
        {
            $executestring += " -o ""$OutputFilePath"""
        }

        if ($null -ne $ProviderFilePath)
        {
            $executestring += " -pf ""$ProviderFilePath"""
        }

        if ($null -ne $Format)
        {
            $executestring += " -f $Format"
        }

        if ($MinBuffers -ne 0 -or $MaxBuffers -ne 256)
        {
            $executestring += " -nb $MinBuffers $MaxBuffers"
        }

        if ($BufferSizeInKB -ne 0)
        {
            $executestring += " -bs $BufferSizeInKB"
        }

        if ($MaxLogFileSizeInMB -ne 0)
        {
            $executestring += " -max $MaxLogFileSizeInMB"
        }

        & $script:Logman $executestring.Split(" ")
    }
}

function Stop-Trace
{
    param(
    [Parameter(Mandatory=$true,
               Position=0)]
    $SessionName,
    [Parameter()]
    [switch]
    $ETS
    )

    Process
    {
        if ($ETS)
        {
            & $script:Logman update $SessionName -ets
            & $script:Logman stop $SessionName -ets
        }
        else
        {
            & $script:Logman update $SessionName
            & $script:Logman stop $SessionName
        }
    }
}

function Enable-WSManTrace
{

    # winrm
    "{04c6e16d-b99f-4a3a-9b3e-b8325bbc781e} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii

    # winrsmgr
    "{c0a36be8-a515-4cfa-b2b6-2676366efff7} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii -Append

    # WinrsExe
    "{f1cab2c0-8beb-4fa2-90e1-8f17e0acdd5d} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii -Append

    # WinrsCmd
    "{03992646-3dfe-4477-80e3-85936ace7abb} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii -Append

    # IPMIPrv
    "{651d672b-e11f-41b7-add3-c2f6a4023672} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii -Append

    #IpmiDrv
    "{D5C6A3E9-FA9C-434e-9653-165B4FC869E4} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii -Append

    # WSManProvHost
    "{6e1b64d7-d3be-4651-90fb-3583af89d7f1} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii -Append

    # Event Forwarding
    "{6FCDF39A-EF67-483D-A661-76D715C6B008} 0xffffffff 0xff" | Out-File $script:wsmprovfile -Encoding ascii -Append

    Start-Trace -SessionName $script:wsmsession -ETS -OutputFilePath $script:wsmanlogfile -Format bincirc -MinBuffers 16 -MaxBuffers 256 -BufferSizeInKB 64 -MaxLogFileSizeInMB 256 -ProviderFilePath $script:wsmprovfile
}

function Disable-WSManTrace
{
    Stop-Trace $script:wsmsession -ETS
}

function Enable-PSWSManCombinedTrace
{
    param (
        [switch] $DoNotOverwriteExistingTrace
    )

    $provfile = [io.path]::GetTempFilename()

    if ($DoNotOverwriteExistingTrace) {
        $fileName = [string][guid]::newguid()
        $logfile = $PSHOME + "\\Traces\\PSTrace_$fileName.etl"
    } else {
        $logfile = $PSHOME + "\\Traces\\PSTrace.etl"
    }

    "$script:psprovidername 0 5" | Out-File $provfile -Encoding ascii
    "$script:wsmprovidername 0 5" | Out-File $provfile -Encoding ascii -Append

    if (!(Test-Path $PSHOME\Traces))
    {
        New-Item -ItemType Directory -Force $PSHOME\Traces | Out-Null
    }

    if (Test-Path $logfile)
    {
        Remove-Item -Force $logfile | Out-Null
    }

    Start-Trace -SessionName $script:pssession -OutputFilePath $logfile -ProviderFilePath $provfile -ETS

    Remove-Item $provfile -Force -ErrorAction SilentlyContinue
}

function Disable-PSWSManCombinedTrace
{
    Stop-Trace -SessionName $script:pssession -ETS
}

function Set-LogProperties
{
    param(
        [Parameter(Mandatory=$true, Position=0, ValueFromPipeline=$true)]
        [Microsoft.PowerShell.Diagnostics.LogDetails]
        $LogDetails,
        [switch] $Force
     )

    Process
    {
        if ($LogDetails.AutoBackup -and !$LogDetails.Retention)
        {
            throw (New-Object System.InvalidOperationException)
        }

        $enabled = $LogDetails.Enabled.ToString()
        $retention = $LogDetails.Retention.ToString()
        $autobackup = $LogDetails.AutoBackup.ToString()
        $maxLogSize = $LogDetails.MaxLogSize.ToString()
        $osVersion = [Version] (Get-CimInstance Win32_OperatingSystem).Version

        if (($LogDetails.Type -eq "Analytic") -or ($LogDetails.Type -eq "Debug"))
        {
            if ($LogDetails.Enabled)
            {
                if($osVersion -lt 6.3.7600)
                {
                    & $script:wevtutil $script:slparam $LogDetails.Name -e:$Enabled
                }
                else
                {
                    & $script:wevtutil /q:$Force $script:slparam $LogDetails.Name -e:$Enabled
                }
            }
            else
            {
                if($osVersion -lt 6.3.7600)
                {
                    & $script:wevtutil $script:slparam $LogDetails.Name -e:$Enabled -rt:$Retention -ms:$MaxLogSize
                }
                else
                {
                    & $script:wevtutil /q:$Force $script:slparam $LogDetails.Name -e:$Enabled -rt:$Retention -ms:$MaxLogSize
                }
            }
        }
        else
        {
            if($osVersion -lt 6.3.7600)
            {
                & $script:wevtutil $script:slparam $LogDetails.Name -e:$Enabled -rt:$Retention -ab:$AutoBackup -ms:$MaxLogSize
            }
            else
            {
                & $script:wevtutil /q:$Force $script:slparam $LogDetails.Name -e:$Enabled -rt:$Retention -ab:$AutoBackup -ms:$MaxLogSize
            }
        }
    }
}

function ConvertTo-Bool([string]$value)
{
    if ($value -ieq "true")
    {
        return $true
    }
    else
    {
        return $false
    }
}

function Get-LogProperties
{
    param(
        [Parameter(Mandatory=$true, ValueFromPipeline=$true, Position=0)] $Name
    )

    Process
    {
        $details = & $script:wevtutil $script:glparam $Name
        $indexes = @(1,2,8,9,10)
        $value = @()
        foreach($index in $indexes)
        {
            $value += @(($details[$index].SubString($details[$index].IndexOf(":")+1)).Trim())
        }

        $enabled = ConvertTo-Bool $value[0]
        $retention = ConvertTo-Bool $value[2]
        $autobackup = ConvertTo-Bool $value[3]

        New-Object Microsoft.PowerShell.Diagnostics.LogDetails $Name, $enabled, $value[1], $retention, $autobackup, $value[4]
    }
}

function Enable-PSTrace
{
    param(
        [switch] $Force,
		[switch] $AnalyticOnly
     )

    $Properties = Get-LogProperties ($script:psprovidername + $script:analyticlog)

	if (!$Properties.Enabled) {
		$Properties.Enabled = $true
		if ($Force) {
			Set-LogProperties $Properties -Force
		} else {
			Set-LogProperties $Properties
		}
	}

	if (!$AnalyticOnly) {
		$Properties = Get-LogProperties ($script:psprovidername + $script:debuglog)
		if (!$Properties.Enabled) {
			$Properties.Enabled = $true
			if ($Force) {
				Set-LogProperties $Properties -Force
			} else {
				Set-LogProperties $Properties
			}
		}
	}
}

function Disable-PSTrace
{
    param(
		[switch] $AnalyticOnly
     )
    $Properties = Get-LogProperties ($script:psprovidername + $script:analyticlog)
	if ($Properties.Enabled) {
		$Properties.Enabled = $false
		Set-LogProperties $Properties
	}

	if (!$AnalyticOnly) {
		$Properties = Get-LogProperties ($script:psprovidername + $script:debuglog)
		if ($Properties.Enabled) {
			$Properties.Enabled = $false
			Set-LogProperties $Properties
		}
	}
}
Add-Type @"
using System;

namespace Microsoft.PowerShell.Diagnostics
{
    public class LogDetails
    {
        public string Name
        {
            get
            {
                return name;
            }
        }
        private string name;

        public bool Enabled
        {
            get
            {
                return enabled;
            }
            set
            {
                enabled = value;
            }
        }
        private bool enabled;

        public string Type
        {
            get
            {
                return type;
            }
        }
        private string type;

        public bool Retention
        {
            get
            {
                return retention;
            }
            set
            {
                retention = value;
            }
        }
        private bool retention;

        public bool AutoBackup
        {
            get
            {
                return autoBackup;
            }
            set
            {
                autoBackup = value;
            }
        }
        private bool autoBackup;

        public int MaxLogSize
        {
            get
            {
                return maxLogSize;
            }
            set
            {
                maxLogSize = value;
            }
        }
        private int maxLogSize;

        public LogDetails(string name, bool enabled, string type, bool retention, bool autoBackup, int maxLogSize)
        {
            this.name = name;
            this.enabled = enabled;
            this.type = type;
            this.retention = retention;
            this.autoBackup = autoBackup;
            this.maxLogSize = maxLogSize;
        }
    }
}
"@

if (Get-Command logman.exe -Type Application -ErrorAction SilentlyContinue)
{
    Export-ModuleMember Disable-PSTrace, Disable-PSWSManCombinedTrace, Disable-WSManTrace, Enable-PSTrace, Enable-PSWSManCombinedTrace, Enable-WSManTrace, Get-LogProperties, Set-LogProperties, Start-Trace, Stop-Trace
}
else
{
    # Currently we only support these cmdlets as logman.exe is not available on systems like Nano and IoT
    Export-ModuleMember Disable-PSTrace, Enable-PSTrace, Get-LogProperties, Set-LogProperties
}

# SIG # Begin signature block
# MIInRAYJKoZIhvcNAQcCoIInNTCCJzECAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCD1N/xHqyvxifWM
# OnpqvYLlOQ8CEvdsrM6YinKwYeiecqCCDLowggX1MIID3aADAgECAhMzAAACHU0Z
# yE7XD1dIAAAAAAIdMA0GCSqGSIb3DQEBCwUAMFcxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAmBgNVBAMTH01pY3Jvc29mdCBD
# b2RlIFNpZ25pbmcgUENBIDIwMjQwHhcNMjYwNDE2MTg1OTQzWhcNMjcwNDE1MTg1
# OTQzWjB0MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
# BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMR4wHAYD
# VQQDExVNaWNyb3NvZnQgQ29ycG9yYXRpb24wggEiMA0GCSqGSIb3DQEBAQUAA4IB
# DwAwggEKAoIBAQDQvewXxx9gZZFC6Ys1WBay8BJ8kGA4JQnH5CMafqOASlTpK9H8
# o5ZXTXt0caVQTNMUPt445wXYD+dFtaKWTwDn1I52oUSrC9vJin1Gsqt+zyKJL5Dg
# 3eQXbQNR61DmMy20GLTIO3SFed9Rfi/ophgCLGFLDR3r0KvHjwMb/jYWS0celV/4
# Lz27LfAekm8v9E5IXaeiXbAUYZKK090n4CVl3JBtbN+9DtI9SNu/yjvozW52/u7R
# X/Ttpa/KDlpuokZ+Zcbvmtd9ur9gFLvZzh41o9MsE/clQtdaFWGvuo6Jua/ntpgk
# ey3E5/vBFe+MJPG6phdnuo6r57ZudCudiI1bAgMBAAGjggGbMIIBlzAOBgNVHQ8B
# Af8EBAMCB4AwHwYDVR0lBBgwFgYKKwYBBAGCN0wIAQYIKwYBBQUHAwMwHQYDVR0O
# BBYEFH6QuMwqcPG0hQlQ6c5jCtTTLrVeMEUGA1UdEQQ+MDykOjA4MR4wHAYDVQQL
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xFjAUBgNVBAUTDTIzMDAxMis1MDc1NTkw
# HwYDVR0jBBgwFoAUf1k/VCHarU/vBeXmo9ctBpQSCDEwYAYDVR0fBFkwVzBVoFOg
# UYZPaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWljcm9zb2Z0
# JTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDI0LmNybDBtBggrBgEFBQcBAQRh
# MF8wXQYIKwYBBQUHMAKGUWh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMv
# Y2VydHMvTWljcm9zb2Z0JTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDI0LmNy
# dDAMBgNVHRMBAf8EAjAAMA0GCSqGSIb3DQEBCwUAA4ICAQBKTbYOjzwTG/DXGaz9
# s6+fQeaTtDcFmMY+5UyVFCyj7Pv+5i37qfX8lSL/tBIfYQfWsMuBQlfZurJD6r4H
# VJ2CeH+1fgiq8dcHdVKoZ3Sa2qXoX3cq9iS8cVb06B7+5/XJ7I0OxHH9fDsvJ3T3
# w5V/ZtAIFmLrl+P0CtG+92uzRsn0nTbdFjOkLMLWPLAU3THohKRlSEMgFJpPkm5n
# 5UAZ35xX6FWCrDLsSKb555bTifwa8mJBwdlof0bmfYidH+dxZ1FdDxvLnNl9zeKs
# A4kejaaIqqIPguhwAti5Ql7BlTNoJNwxCvBmqW2MQLnCkYN/VVUsR3V2x/rcTNzo
# Bf/Z/SpROvdaA2ZOOd1uioXJt3tdLQ7vHpqpib0KfWr/FWXW10q38VxfCnRQBqzb
# SuztR7nEMuzX7Ck+B/XaPDXd1qh72+QYyB0Z2VzWmO9zsnb9Uq/dwu8LGeQqnyu6
# 7SDGACvnXii2fb9+US492VTnXSnFKyqwgzUyFMtZK1/sHYTv6bG4TtQUygQxTN+Z
# V+aJIlKO2MqZ7bKrAnOzS9m6NgoTdWOq11bTOZwKlIEV/EhV9SWkDmdpR/hPPT2v
# 6TEj4F8PT/zHjRezIU5c/DGlt/VhY/pK0XkJtEyMmmS1BMtjU/rqBZVMIm3dnxQs
# /TBByr+Cf8Z1r7aifQVQ+WSqzjCCBr0wggSloAMCAQICEzMAAAA5O7Y3Gb8GHWcA
# AAAAADkwDQYJKoZIhvcNAQEMBQAwgYgxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpX
# YXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQg
# Q29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBSb290IENlcnRpZmljYXRl
# IEF1dGhvcml0eSAyMDExMB4XDTI0MDgwODIwNTQxOFoXDTM2MDMyMjIyMTMwNFow
# VzELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEo
# MCYGA1UEAxMfTWljcm9zb2Z0IENvZGUgU2lnbmluZyBQQ0EgMjAyNDCCAiIwDQYJ
# KoZIhvcNAQEBBQADggIPADCCAgoCggIBANgBnB7jOMeqlRYHNa265v4IY9fH8TKh
# emHfPINe1gpLaV3dhg324WwH06LcHbpnsBukCDNitryo0dtS/EW6I/yEL/bLSY8h
# KpbfQuWusBPr9qazYcDxCW/qnjb5JsI1s8bNOg3bVATvQVL4tcf03aTycsz8QeCd
# M0l/yHRObJ9QqazM1r6VPEOJ7LL+uEEb73w6QCuhs89a1uv1zerOYMnsneRRwCbp
# yW11IcggU0cRKDDq1pjVJzIbIF6+oiXXbReOsgeI8zu1FyQfK0fVkaya8SmVHQ/t
# Of23mZ4W9k0Ri22QW9p3UgSC5OUDktKxxcCmGL6tXLfOGSWHIIV4YrTJTT6PNty5
# REojHJuZHArkF9VnHTERWoTjAzfI3kP+5b4alUdhgAZ7ttOu1bVnXfHaqPYl2rPs
# 20ji03LOVWsh/radgE17es5hL+t6lV0eVHrVhsssROWJuz2MXMCt7iw7lFPG9LXK
# Gjsmonn2gotGdHIuEg5JnJMJVmixd5LRlkmgYRZKzhxSCwyoGIq0PhaA7Y+VPct5
# pCHkijcIIDm0nlkK+0KyepolcqGm0T/GYQRMhHJlGOOmVQop36wUVUYklUy++vDW
# eEgEo4s7hxN6mIbf2MSIQ/iIfMZgJxC69oukMUXCrOC3SkE/xIkgpfl22MM1itkZ
# 35nNXkMolU1lAgMBAAGjggFOMIIBSjAOBgNVHQ8BAf8EBAMCAYYwEAYJKwYBBAGC
# NxUBBAMCAQAwHQYDVR0OBBYEFH9ZP1Qh2q1P7wXl5qPXLQaUEggxMBkGCSsGAQQB
# gjcUAgQMHgoAUwB1AGIAQwBBMA8GA1UdEwEB/wQFMAMBAf8wHwYDVR0jBBgwFoAU
# ci06AjGQQ7kUBU7h6qfHMdEjiTQwWgYDVR0fBFMwUTBPoE2gS4ZJaHR0cDovL2Ny
# bC5taWNyb3NvZnQuY29tL3BraS9jcmwvcHJvZHVjdHMvTWljUm9vQ2VyQXV0MjAx
# MV8yMDExXzAzXzIyLmNybDBeBggrBgEFBQcBAQRSMFAwTgYIKwYBBQUHMAKGQmh0
# dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2kvY2VydHMvTWljUm9vQ2VyQXV0MjAx
# MV8yMDExXzAzXzIyLmNydDANBgkqhkiG9w0BAQwFAAOCAgEAFJQfOChP7onn6fLI
# MKrSlN1WYKwDFgAddymOUO3FrM8d7B/W/iQ6DxXsDn7D5W4wMwYeLystcEqfkjz4
# NURRgazyMu5yRzQh4LqjA4tStTcJh1opExo7nn5PuPBYnbu0+THSuVHTe0VTTPVh
# ily/piFrDo3axQ9P4C+Ol5yet+2gTfekICS5xS+cYfSIvgn0JksVBVMYVI5QFu/q
# hnLhsEFEUzG8fvv0hjgkO+lkpV9ty6GkN4vdnd7ya6Q6aR9y34aiM1qmxaxBi6OU
# nyNl6fkuun/diTFnYDLTppOkr/mg5WSfCiDVMNCxtj4wPKC5OmHm1DQIt/MNokbb
# H3UGsFP1QbzsLocuSqLCvH09Io3fDPTmscR9Y75G4qX7RTX8AdBPo0I6OEojf39z
# uFZt0qOHm65YWQE69cZM2ueE1MB05dNNgHK9gTE7zKvK/fg8B2qjW88MT/WF5V5u
# vZGtqa9FSL2RazArA+rDPuf6JGYz4HpgMZHB4S6szWSKYBv0VisCzfxgeU+dquXW
# 9bd0auYlOB58DPcOYKdc3Se94g+xL4pcEhbB54JOgAkwYTu/9dLeH2pDqeJZAABV
# DWRQCaXfO5LgyKwKCLYXpigrZYCjUSBcr+Ve8PFWMhVTQl0v4q8J/AUmQN5W4n10
# 1cY2L4A7GTQG1h32HHAvfQESWP0xghngMIIZ3AIBATBuMFcxCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAmBgNVBAMTH01pY3Jv
# c29mdCBDb2RlIFNpZ25pbmcgUENBIDIwMjQCEzMAAAIdTRnITtcPV0gAAAAAAh0w
# DQYJYIZIAWUDBAIBBQCggZAwGQYJKoZIhvcNAQkDMQwGCisGAQQBgjcCAQQwLwYJ
# KoZIhvcNAQkEMSIEIFmJjkOHA8LpNjOjiyWRffMk9VinEtkX++kJerrkV5IQMEIG
# CisGAQQBgjcCAQwxNDAyoBSAEgBNAGkAYwByAG8AcwBvAGYAdKEagBhodHRwOi8v
# d3d3Lm1pY3Jvc29mdC5jb20wDQYJKoZIhvcNAQEBBQAEggEAIUI2w6Jt4s0Jx8cd
# dsoOKLTHy2zp1zyH20JW/ccTemquGNaeMPVGZieMZBE2hZIvcoledQB1JRRibbfy
# EeT8ISsnIMa0NSprxLFx4YSouczIERkVDaSGjVneU2o6cyMSRZntFIUmlUkhYffP
# I7+Mfa5aqqEif/yNQgiT8v8GiRiy0w/5/qLgKzhxR782KpSzrNDhvWaniUltCgUn
# mF76lBN+c+vnMRVb4+QtLcCyp4iilG7dehROiWNPSBQCNz8qu08R61Ng4Hls4d7M
# Hp+c9638aY9b/oR5fsNl/4+BF90zHRWm2I26kkRtzRdZ87KvYX8OCn2v7EquGeGr
# DrOcMaGCF7AwghesBgorBgEEAYI3AwMBMYIXnDCCF5gGCSqGSIb3DQEHAqCCF4kw
# gheFAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFaBgsqhkiG9w0BCRABBKCCAUkEggFF
# MIIBQQIBAQYKKwYBBAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCBQlbQIiUEippHZ
# yZrwZOxDX0t/RtEMmVlGGgZKWwDw1gIGauvccDTzGBMyMDI2MTEwOTIyNDI0Ni45
# ODlaMASAAgH0oIHZpIHWMIHTMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
# Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
# cmF0aW9uMS0wKwYDVQQLEyRNaWNyb3NvZnQgSXJlbGFuZCBPcGVyYXRpb25zIExp
# bWl0ZWQxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo1MjFBLTA1RTAtRDk0NzEl
# MCMGA1UEAxMcTWljcm9zb2Z0IFRpbWUtU3RhbXAgU2VydmljZaCCEf4wggcoMIIF
# EKADAgECAhMzAAACOUfhUUUnv6RMAAEAAAI5MA0GCSqGSIb3DQEBCwUAMHwxCzAJ
# BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25k
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jv
# c29mdCBUaW1lLVN0YW1wIFBDQSAyMDEwMB4XDTI2MTAxNTE4Mzc0OFoXDTI4MDEx
# MTE4Mzc0OFowgdMxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
# DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24x
# LTArBgNVBAsTJE1pY3Jvc29mdCBJcmVsYW5kIE9wZXJhdGlvbnMgTGltaXRlZDEn
# MCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjUyMUEtMDVFMC1EOTQ3MSUwIwYDVQQD
# ExxNaWNyb3NvZnQgVGltZS1TdGFtcCBTZXJ2aWNlMIICIjANBgkqhkiG9w0BAQEF
# AAOCAg8AMIICCgKCAgEAuzMLiBSvpO1XyRFSetAOq9jrCbOOOUYFKLog4xjeoYTB
# Kv5Kzdscdosj/eAhKgTmEKvV6/EdptAxbe0koJckkoWY9neGTpseHDfhi0nPLyc0
# lMl8pLSuympdJUVegHRrvMVy0ud6/F0t6OD1a1RNNxdqxi/knDug1gFZsDdpcabN
# 5Cvkta0HUgklqUKAsGp53lSB3AobZZyts+rvvbVM1Wj8geHFuLzshtagjeiYx4wu
# ntgPOinXYIrT5Z1ANRJVfNG5/7fJLW5P6dssJW/ICfb3Lix+jNPn+je9wvsGwNKo
# AdIGzQeeMdeuC4ehx4cLZzK4TzbD2AwKLKr5fbBBu0+UFUSc6ONPDDFyobjKPVkW
# F6lhZ/yJ478nlFzVd++LTyFiewCg+8MTa2A8qwfNOn2piQRu8kuS/BMNOxsNyeZC
# ReOkZt1q8V29bDSOASjPXIwe99ruVRSZyaCLUhFUenIfLBax7XkzGvLOmDdy9o0e
# mJbNz0wWIlRw5HGNXwOf5FXyO6lDnjVuwXJ0nmjsTKwjwYMPevkvaxe2lFBGUaL5
# pXWvtEbSNiSxBYeXC0XL3EPYTco1/Q5oyek9fY8r7kSHFG2sGPgtCDWfpVVw1pj/
# 9AXrXjP62GuUiLZc5LOnwBR32ZenVu79aAIRLeIQNTfoOfM46dI9e0pV9u3JmH0C
# AwEAAaOCAUkwggFFMB0GA1UdDgQWBBQsltpuAX4qBqdjFXGLRhdXHeF/mjAfBgNV
# HSMEGDAWgBSfpxVdAF5iXYP05dJlpxtTNRnpcjBfBgNVHR8EWDBWMFSgUqBQhk5o
# dHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBU
# aW1lLVN0YW1wJTIwUENBJTIwMjAxMCgxKS5jcmwwbAYIKwYBBQUHAQEEYDBeMFwG
# CCsGAQUFBzAChlBodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRz
# L01pY3Jvc29mdCUyMFRpbWUtU3RhbXAlMjBQQ0ElMjAyMDEwKDEpLmNydDAMBgNV
# HRMBAf8EAjAAMBYGA1UdJQEB/wQMMAoGCCsGAQUFBwMIMA4GA1UdDwEB/wQEAwIH
# gDANBgkqhkiG9w0BAQsFAAOCAgEAKLalk/XzSS3atGSe/bAjyuD22ADLtzI0LEC3
# yBOQsSTE/9WsVTVYzCeATQ3+/CCxHRIofS2jlFTXPmYtj5nwlahiR8m0lC5qjXqj
# F4ufq9kx3h6f/j0jmy4+Eqh5cBmsLLvJvj4Om6iJTc+Vxu50SE+C3dgxRl9LDSm/
# IU9Q3mbjrS26v9dhKNqTwyX0HEAX5L4KLbiG4f95w3UfzbC5jXQFPgl11Oh1Wzmp
# isQX1Kuee5wHikNbwjQLqijWR14Za5uWpn0Y5O9koS7+4aqv6C29Up7x1oWinULB
# n7bhEvsgfbUDblP0WG5z6JuGTQBaEJh3VQBtgNsAfoVnuA7vrxGHFJw36i75ksY3
# m3P5Ef5ajqo2clASlbFEqX1KoXE201fvqlwyyZWsK+PWnLoq1/s2F0creICvR3pV
# QfWTUCMv4C6JRvesvcoa9Zzv4uc4mJWk20NDrjuE9LArYV0uKm68//2ZtJrs00sp
# MRHJ3TX6hkD/gtLYzFJ3edgEV6NbX2kOfxxkQKES2sS+SpG5ohXO8tVVp0OFWYzA
# 2UOwip6+pE012E41x3QryjCC4NL6iacS1djYb8N33AcU8K6GlCscJhQAPzoE2ZRF
# Cix5F4o4L0SvIVqLz6JjD+rXZTyXOOKKLKvtEDnkH/ZFmlwC07ltI9kp/j9TtzKX
# X/26u1MwggdxMIIFWaADAgECAhMzAAAAFcXna54Cm0mZAAAAAAAVMA0GCSqGSIb3
# DQEBCwUAMIGIMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4G
# A1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTIw
# MAYDVQQDEylNaWNyb3NvZnQgUm9vdCBDZXJ0aWZpY2F0ZSBBdXRob3JpdHkgMjAx
# MDAeFw0yMTA5MzAxODIyMjVaFw0zMDA5MzAxODMyMjVaMHwxCzAJBgNVBAYTAlVT
# MRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29mdCBUaW1l
# LVN0YW1wIFBDQSAyMDEwMIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA
# 5OGmTOe0ciELeaLL1yR5vQ7VgtP97pwHB9KpbE51yMo1V/YBf2xK4OK9uT4XYDP/
# XE/HZveVU3Fa4n5KWv64NmeFRiMMtY0Tz3cywBAY6GB9alKDRLemjkZrBxTzxXb1
# hlDcwUTIcVxRMTegCjhuje3XD9gmU3w5YQJ6xKr9cmmvHaus9ja+NSZk2pg7uhp7
# M62AW36MEBydUv626GIl3GoPz130/o5Tz9bshVZN7928jaTjkY+yOSxRnOlwaQ3K
# Ni1wjjHINSi947SHJMPgyY9+tVSP3PoFVZhtaDuaRr3tpK56KTesy+uDRedGbsoy
# 1cCGMFxPLOJiss254o2I5JasAUq7vnGpF1tnYN74kpEeHT39IM9zfUGaRnXNxF80
# 3RKJ1v2lIH1+/NmeRd+2ci/bfV+AutuqfjbsNkz2K26oElHovwUDo9Fzpk03dJQc
# NIIP8BDyt0cY7afomXw/TNuvXsLz1dhzPUNOwTM5TI4CvEJoLhDqhFFG4tG9ahha
# YQFzymeiXtcodgLiMxhy16cg8ML6EgrXY28MyTZki1ugpoMhXV8wdJGUlNi5UPkL
# iWHzNgY1GIRH29wb0f2y1BzFa/ZcUlFdEtsluq9QBXpsxREdcu+N+VLEhReTwDwV
# 2xo3xwgVGD94q0W29R6HXtqPnhZyacaue7e3PmriLq0CAwEAAaOCAd0wggHZMBIG
# CSsGAQQBgjcVAQQFAgMBAAEwIwYJKwYBBAGCNxUCBBYEFCqnUv5kxJq+gpE8RjUp
# zxD/LwTuMB0GA1UdDgQWBBSfpxVdAF5iXYP05dJlpxtTNRnpcjBcBgNVHSAEVTBT
# MFEGDCsGAQQBgjdMg30BATBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jv
# c29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0wEwYDVR0lBAwwCgYI
# KwYBBQUHAwgwGQYJKwYBBAGCNxQCBAweCgBTAHUAYgBDAEEwCwYDVR0PBAQDAgGG
# MA8GA1UdEwEB/wQFMAMBAf8wHwYDVR0jBBgwFoAU1fZWy4/oolxiaNE9lJBb186a
# GMQwVgYDVR0fBE8wTTBLoEmgR4ZFaHR0cDovL2NybC5taWNyb3NvZnQuY29tL3Br
# aS9jcmwvcHJvZHVjdHMvTWljUm9vQ2VyQXV0XzIwMTAtMDYtMjMuY3JsMFoGCCsG
# AQUFBwEBBE4wTDBKBggrBgEFBQcwAoY+aHR0cDovL3d3dy5taWNyb3NvZnQuY29t
# L3BraS9jZXJ0cy9NaWNSb29DZXJBdXRfMjAxMC0wNi0yMy5jcnQwDQYJKoZIhvcN
# AQELBQADggIBAJ1VffwqreEsH2cBMSRb4Z5yS/ypb+pcFLY+TkdkeLEGk5c9MTO1
# OdfCcTY/2mRsfNB1OW27DzHkwo/7bNGhlBgi7ulmZzpTTd2YurYeeNg2LpypglYA
# A7AFvonoaeC6Ce5732pvvinLbtg/SHUB2RjebYIM9W0jVOR4U3UkV7ndn/OOPcbz
# aN9l9qRWqveVtihVJ9AkvUCgvxm2EhIRXT0n4ECWOKz3+SmJw7wXsFSFQrP8DJ6L
# GYnn8AtqgcKBGUIZUnWKNsIdw2FzLixre24/LAl4FOmRsqlb30mjdAy87JGA0j3m
# Sj5mO0+7hvoyGtmW9I/2kQH2zsZ0/fZMcm8Qq3UwxTSwethQ/gpY3UA8x1RtnWN0
# SCyxTkctwRQEcb9k+SS+c23Kjgm9swFXSVRk2XPXfx5bRAGOWhmRaw2fpCjcZxko
# JLo4S5pu+yFUa2pFEUep8beuyOiJXk+d0tBMdrVXVAmxaQFEfnyhYWxz/gq77EFm
# PWn9y8FBSX5+k77L+DvktxW/tM4+pTFRhLy/AsGConsXHRWJjXD+57XQKBqJC482
# 2rpM+Zv/Cuk0+CQ1ZyvgDbjmjJnW4SLq8CdCPSWU5nR0W2rRnj7tfqAxM328y+l7
# vzhwRNGQ8cirOoo6CGJ/2XBjU02N7oJtpQUQwXEGahC0HVUzWLOhcGbyoYIDWTCC
# AkECAQEwggEBoYHZpIHWMIHTMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
# Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
# cmF0aW9uMS0wKwYDVQQLEyRNaWNyb3NvZnQgSXJlbGFuZCBPcGVyYXRpb25zIExp
# bWl0ZWQxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo1MjFBLTA1RTAtRDk0NzEl
# MCMGA1UEAxMcTWljcm9zb2Z0IFRpbWUtU3RhbXAgU2VydmljZaIjCgEBMAcGBSsO
# AwIaAxUAs2iw/gPTquN43LBFziljq2fTf2CggYMwgYCkfjB8MQswCQYDVQQGEwJV
# UzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UE
# ChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGlt
# ZS1TdGFtcCBQQ0EgMjAxMDANBgkqhkiG9w0BAQsFAAIFAO6cSS0wIhgPMjAyNjEx
# MDkxMzI5MTdaGA8yMDI2MTExMDEzMjkxN1owdzA9BgorBgEEAYRZCgQBMS8wLTAK
# AgUA7pxJLQIBADAKAgEAAgIT6gIB/zAHAgEAAgISoDAKAgUA7p2arQIBADA2Bgor
# BgEEAYRZCgQCMSgwJjAMBgorBgEEAYRZCgMCoAowCAIBAAIDB6EgoQowCAIBAAID
# AYagMA0GCSqGSIb3DQEBCwUAA4IBAQCIW6zOr8IWjpmlkGVEldG0jaqR/6V6iCj3
# iKDyX54/LWEONERrmI+SIwWJE3OoICJ0LKV6OK0WWd/3m85qnHmNLbgeIDTti3Ib
# 5I9K6voQzdChIWsgnZFGgb7fZcqEtDKHSgX/tw8l5vCitAJPgmBQRqS9PaLrVWI/
# 76aQZUX6e9kH+xOnRZkTDt5acJeiYQ1dqeqt/sTBWc3iv/vD88DY0GKPLfs7X/8+
# Lob8IY9RD9QKe9a0jVMppiTtkUfmNaMizoSzhBEUL4oYoGtyHRAt31tCX1RQ+suS
# sxb1ih/2Vlan1JbUxHbsZ3W2Z7YL6+t525j3XF67EUj8e6/B8jLxMYIEDTCCBAkC
# AQEwgZMwfDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNV
# BAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQG
# A1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIwMTACEzMAAAI5R+FRRSe/
# pEwAAQAAAjkwDQYJYIZIAWUDBAIBBQCgggFKMBoGCSqGSIb3DQEJAzENBgsqhkiG
# 9w0BCRABBDAvBgkqhkiG9w0BCQQxIgQgdgcj2mJlXFdUNLx6kSPPxgRxbyX0ra+j
# fONqgSCrU0YwgfoGCyqGSIb3DQEJEAIvMYHqMIHnMIHkMIG9BCADcfIxbWTODDpE
# Sd0tl1sGk04hohF4h9q1YbHp5ilW5TCBmDCBgKR+MHwxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
# aWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29mdCBUaW1lLVN0
# YW1wIFBDQSAyMDEwAhMzAAACOUfhUUUnv6RMAAEAAAI5MCIEIAPTArw4Ni63mP5O
# lt6dmAtzep1cSrt8HPkZSMoGUg6iMA0GCSqGSIb3DQEBCwUABIICAGJYKFNhuo5b
# 16EoTdt/kHxpYesTvnNYGsjpkGnwYZNE0Qwjt0MNFpdYQfOJVtCI6lQx6ePWuHC9
# Th/+6JjTr4t75fwJr3a5CMNSvPu79ObfizEC3Zc7VfNna732k0Q/OXTxeWEE9gGy
# zIft7dc7vor0MuJuhquXV8wHImQquF9VynqZQGh6uCOdt0c6B8iNouCteI/9qRxD
# r7NOkBRKfXFlK5z8jxFexceyG2PZQx7qIeClq2314twAqduB3YMBq3Q/RtBnCzrD
# 3/gb3ucVrGlLG7sTe68NDHzfOiwOmNFBiPVfRTTB5n4MmeVoL4yqpTfg2AuiKNbs
# EMF6mYaQwMS13wmiYZKqb1dA+eaYPOBaZ+tTew/T0ssBMckmht2lTwMCSKvmOC9S
# w55NuXeoU8jgaA0c24Hw74Vx7Oz8pkxTa96m3RH4VEWhdDbAijuQ72iRFAhBhBqU
# 20VOd8TiW1eSLWnMB6QBh/jqcSkoV9APMqgXBdSODBsbigdQBtYOpJWwO7rTWdJc
# YFMvwhjk6TbD/eVZ51K01v6bs/YiPkIShuPxPpsERmQHTU7RjEoFJgsTzY2E1rWO
# OcYVeMCiru4O+NtyFEfNt5aQHlErE9EI4uG5hHaURCPYfrduBJ/qXu5tQzfxQJNy
# 1nAj5bQWGuMB1bRk6iARsZuAAjzy8SXg
# SIG # End signature block
