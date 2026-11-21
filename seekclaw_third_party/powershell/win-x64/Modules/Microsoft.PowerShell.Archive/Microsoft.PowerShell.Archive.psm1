# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

data LocalizedData
{
    # culture="en-US"
    ConvertFrom-StringData @'
    PathNotFoundError=The path '{0}' either does not exist or is not a valid file system path.
    ExpandArchiveInValidDestinationPath=The path '{0}' is not a valid file system directory path.
    InvalidZipFileExtensionError={0} is not a supported archive file format. {1} is the only supported archive file format.
    ArchiveFileIsReadOnly=The attributes of the archive file {0} is set to 'ReadOnly' hence it cannot be updated. If you intend to update the existing archive file, remove the 'ReadOnly' attribute on the archive file else use -Force parameter to override and create a new archive file.
    ZipFileExistError=The archive file {0} already exists. Use the -Update parameter to update the existing archive file or use the -Force parameter to overwrite the existing archive file.
    DuplicatePathFoundError=The input to {0} parameter contains a duplicate path '{1}'. Provide a unique set of paths as input to {2} parameter.
    ArchiveFileIsEmpty=The archive file {0} is empty.
    CompressProgressBarText=The archive file '{0}' creation is in progress...
    ExpandProgressBarText=The archive file '{0}' expansion is in progress...
    ExpandProgressBarBeforeExtractionText=The archive file '{0}' entries were evaluated and expansion is in progress...
    ExpandProgressBarAfterExtractionText=The archive file '{0}' expansion attempt has finished...
    AppendArchiveFileExtensionMessage=The archive file path '{0}' supplied to the DestinationPath parameter does not include .zip extension. Hence .zip is appended to the supplied DestinationPath path and the archive file would be created at '{1}'.
    AddItemtoArchiveFile=Adding '{0}'.
    BadArchiveEntry=Can not process invalid archive entry '{0}'.
    ReservedDeviceNameInArchiveEntry=The archive entry '{0}' contains a Windows reserved device name as one of its segments which is not supported. Rename it to '{1}' and run the command again.
    CreateFileAtExpandedPath=Created '{0}'.
    InvalidArchiveFilePathError=The archive file path '{0}' specified as input to the {1} parameter is resolving to multiple file system paths. Provide a unique path to the {2} parameter where the archive file has to be created.
    InvalidExpandedDirPathError=The directory path '{0}' specified as input to the DestinationPath parameter is resolving to multiple file system paths. Provide a unique path to the Destination parameter where the archive file contents have to be expanded.
    FileExistsError=Failed to create file '{0}' while expanding the archive file '{1}' contents as the file '{2}' already exists. Use the -Force parameter if you want to overwrite the existing directory '{3}' contents when expanding the archive file.
    DeleteArchiveFile=The partially created archive file '{0}' is deleted as it is not usable.
    InvalidDestinationPath=The destination path '{0}' does not contain a valid archive file name.
    PreparingToCompressVerboseMessage=Preparing to compress...
    PreparingToExpandVerboseMessage=Preparing to expand...
    FailedToExpandVerboseMessage=Failed to expand the archive file. If there was an error while extracting the archive, the archive will remain partially extracted at '{0}'.
    EvaluatingArchiveEntries=Evaluating the archive file entries for potential issues before expansion.
    CompletedEvaluatingArchiveEntries=Completed evaluating the archive file entries for potential issues before expansion.
    ItemDoesNotAppearToBeAValidZipArchive=File '{0}' does not appear to be a valid zip archive.
'@
}

Import-LocalizedData LocalizedData -filename ArchiveResources -ErrorAction Ignore

$zipFileExtension = ".zip"

# Reserved Windows device names used by IsValidWindowsArchiveEntryPath to guard Expand-Archive.
$script:reservedDeviceNames = New-Object -TypeName 'System.Collections.Generic.HashSet[string]' -ArgumentList @(
    [string[]]@('CON','PRN','AUX','NUL',
                'COM1','COM2','COM3','COM4','COM5','COM6','COM7','COM8','COM9', 'COM¹', 'COM²', 'COM³',
                'LPT1','LPT2','LPT3','LPT4','LPT5','LPT6','LPT7','LPT8','LPT9', 'LPT¹', 'LPT²', 'LPT³'),
    [System.StringComparer]::OrdinalIgnoreCase)

<############################################################################################
# The Compress-Archive cmdlet can be used to zip/compress one or more files/directories.
############################################################################################>
function Compress-Archive
{
    [CmdletBinding(
    DefaultParameterSetName="Path",
    SupportsShouldProcess=$true,
    HelpUri="https://go.microsoft.com/fwlink/?linkid=2096473")]
    [OutputType([System.IO.File])]
    param
    (
        [parameter (mandatory=$true, Position=0, ParameterSetName="Path", ValueFromPipeline=$true, ValueFromPipelineByPropertyName=$true)]
        [parameter (mandatory=$true, Position=0, ParameterSetName="PathWithForce", ValueFromPipeline=$true, ValueFromPipelineByPropertyName=$true)]
        [parameter (mandatory=$true, Position=0, ParameterSetName="PathWithUpdate", ValueFromPipeline=$true, ValueFromPipelineByPropertyName=$true)]
        [ValidateNotNullOrEmpty()]
        [string[]] $Path,

        [parameter (mandatory=$true, ParameterSetName="LiteralPath", ValueFromPipeline=$false, ValueFromPipelineByPropertyName=$true)]
        [parameter (mandatory=$true, ParameterSetName="LiteralPathWithForce", ValueFromPipeline=$false, ValueFromPipelineByPropertyName=$true)]
        [parameter (mandatory=$true, ParameterSetName="LiteralPathWithUpdate", ValueFromPipeline=$false, ValueFromPipelineByPropertyName=$true)]
        [ValidateNotNullOrEmpty()]
        [Alias("PSPath")]
        [string[]] $LiteralPath,

        [parameter (mandatory=$true,
        Position=1,
        ValueFromPipeline=$false,
        ValueFromPipelineByPropertyName=$false)]
        [ValidateNotNullOrEmpty()]
        [string] $DestinationPath,

        [parameter (
        mandatory=$false,
        ValueFromPipeline=$false,
        ValueFromPipelineByPropertyName=$false)]
        [ValidateSet("Optimal","NoCompression","Fastest")]
        [string]
        $CompressionLevel = "Optimal",

        [parameter(mandatory=$true, ParameterSetName="PathWithUpdate", ValueFromPipeline=$false, ValueFromPipelineByPropertyName=$false)]
        [parameter(mandatory=$true, ParameterSetName="LiteralPathWithUpdate", ValueFromPipeline=$false, ValueFromPipelineByPropertyName=$false)]
        [switch]
        $Update = $false,

        [parameter(mandatory=$true, ParameterSetName="PathWithForce", ValueFromPipeline=$false, ValueFromPipelineByPropertyName=$false)]
        [parameter(mandatory=$true, ParameterSetName="LiteralPathWithForce", ValueFromPipeline=$false, ValueFromPipelineByPropertyName=$false)]
        [switch]
        $Force = $false,

        [switch]
        $PassThru = $false
    )

    BEGIN
    {
        # Ensure the destination path is in a non-PS-specific format
        $DestinationPath = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($DestinationPath)

        $inputPaths = @()
        $destinationParentDir = [system.IO.Path]::GetDirectoryName($DestinationPath)
        if($null -eq $destinationParentDir)
        {
            $errorMessage = ($LocalizedData.InvalidDestinationPath -f $DestinationPath)
            ThrowTerminatingErrorHelper "InvalidArchiveFilePath" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $DestinationPath
        }

        if($destinationParentDir -eq [string]::Empty)
        {
            $destinationParentDir = '.'
        }

        $archiveFileName = [system.IO.Path]::GetFileName($DestinationPath)
        $destinationParentDir = GetResolvedPathHelper $destinationParentDir $false $PSCmdlet

        if($destinationParentDir.Count -gt 1)
        {
            $errorMessage = ($LocalizedData.InvalidArchiveFilePathError -f $DestinationPath, "DestinationPath", "DestinationPath")
            ThrowTerminatingErrorHelper "InvalidArchiveFilePath" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $DestinationPath
        }

        IsValidFileSystemPath $destinationParentDir | Out-Null
        $DestinationPath = Join-Path -Path $destinationParentDir -ChildPath $archiveFileName

        # GetExtension API does not validate for the actual existence of the path.
        $extension = [system.IO.Path]::GetExtension($DestinationPath)

        # If user does not specify an extension, we append the .zip extension automatically.
        If($extension -eq [string]::Empty)
        {
            $DestinationPathWithOutExtension = $DestinationPath
            $DestinationPath = $DestinationPathWithOutExtension + $zipFileExtension
            $appendArchiveFileExtensionMessage = ($LocalizedData.AppendArchiveFileExtensionMessage -f $DestinationPathWithOutExtension, $DestinationPath)
            Write-Verbose $appendArchiveFileExtensionMessage
        }

        $archiveFileExist = Test-Path -LiteralPath $DestinationPath -PathType Leaf

        if($archiveFileExist -and ($Update -eq $false -and $Force -eq $false))
        {
            $errorMessage = ($LocalizedData.ZipFileExistError -f $DestinationPath)
            ThrowTerminatingErrorHelper "ArchiveFileExists" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $DestinationPath
        }

        # If archive file already exists and if -Update is specified, then we check to see
        # if we have write access permission to update the existing archive file.
        if($archiveFileExist -and $Update -eq $true)
        {
            $item = Get-Item -Path $DestinationPath
            if($item.Attributes.ToString().Contains("ReadOnly"))
            {
                $errorMessage = ($LocalizedData.ArchiveFileIsReadOnly -f $DestinationPath)
                ThrowTerminatingErrorHelper "ArchiveFileIsReadOnly" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidOperation) $DestinationPath
            }
        }

        $isWhatIf = $psboundparameters.ContainsKey("WhatIf")
        if(!$isWhatIf)
        {
            $preparingToCompressVerboseMessage = ($LocalizedData.PreparingToCompressVerboseMessage)
            Write-Verbose $preparingToCompressVerboseMessage

            $progressBarStatus = ($LocalizedData.CompressProgressBarText -f $DestinationPath)
            ProgressBarHelper "Compress-Archive" $progressBarStatus 0 100 100 1
        }
    }
    PROCESS
    {
        if($PsCmdlet.ParameterSetName -eq "Path" -or
        $PsCmdlet.ParameterSetName -eq "PathWithForce" -or
        $PsCmdlet.ParameterSetName -eq "PathWithUpdate")
        {
            $inputPaths += $Path
        }

        if($PsCmdlet.ParameterSetName -eq "LiteralPath" -or
        $PsCmdlet.ParameterSetName -eq "LiteralPathWithForce" -or
        $PsCmdlet.ParameterSetName -eq "LiteralPathWithUpdate")
        {
            $inputPaths += $LiteralPath
        }
    }
    END
    {
        # If archive file already exists and if -Force is specified, we delete the
        # existing archive file and create a brand new one.
        if(($PsCmdlet.ParameterSetName -eq "PathWithForce" -or
        $PsCmdlet.ParameterSetName -eq "LiteralPathWithForce") -and $archiveFileExist)
        {
            Remove-Item -Path $DestinationPath -Force -ErrorAction Stop
        }

        # Validate Source Path depending on parameter set being used.
        # The specified source path contains one or more files or directories that needs
        # to be compressed.
        $isLiteralPathUsed = $false
        if($PsCmdlet.ParameterSetName -eq "LiteralPath" -or
        $PsCmdlet.ParameterSetName -eq "LiteralPathWithForce" -or
        $PsCmdlet.ParameterSetName -eq "LiteralPathWithUpdate")
        {
            $isLiteralPathUsed = $true
        }

        ValidateDuplicateFileSystemPath $PsCmdlet.ParameterSetName $inputPaths
        $resolvedPaths = GetResolvedPathHelper $inputPaths $isLiteralPathUsed $PSCmdlet
        IsValidFileSystemPath $resolvedPaths | Out-Null

        $sourcePath = $resolvedPaths;

        # CSVHelper: This is a helper function used to append comma after each path specified by
        # the $sourcePath array. The comma separated paths are displayed in the -WhatIf message.
        $sourcePathInCsvFormat = CSVHelper $sourcePath
        if($pscmdlet.ShouldProcess($sourcePathInCsvFormat))
        {
            try
            {
                # StopProcessing is not available in Script cmdlets. However the pipeline execution
                # is terminated when ever 'CTRL + C' is entered by user to terminate the cmdlet execution.
                # The finally block is executed whenever pipeline is terminated.
                # $isArchiveFileProcessingComplete variable is used to track if 'CTRL + C' is entered by the
                # user.
                $isArchiveFileProcessingComplete = $false

                $numberOfItemsArchived = CompressArchiveHelper $sourcePath $DestinationPath $CompressionLevel $Update

                $isArchiveFileProcessingComplete = $true
            }
            finally
            {
                # The $isArchiveFileProcessingComplete would be set to $false if user has typed 'CTRL + C' to
                # terminate the cmdlet execution or if an unhandled exception is thrown.
                # $numberOfItemsArchived contains the count of number of files or directories add to the archive file.
                # If the newly created archive file is empty then we delete it as it's not usable.
                if(($isArchiveFileProcessingComplete -eq $false) -or
                ($numberOfItemsArchived -eq 0))
                {
                    $DeleteArchiveFileMessage = ($LocalizedData.DeleteArchiveFile -f $DestinationPath)
                    Write-Verbose $DeleteArchiveFileMessage

                    # delete the partial archive file created.
                    if (Test-Path $DestinationPath) {
                        Remove-Item -LiteralPath $DestinationPath -Force -Recurse -ErrorAction SilentlyContinue
                    }
                }
                elseif ($PassThru)
                {
                    Get-Item -LiteralPath $DestinationPath
                }
            }
        }
    }
}

<############################################################################################
# The Expand-Archive cmdlet can be used to expand/extract an zip file.
############################################################################################>
function Expand-Archive
{
    [CmdletBinding(
    DefaultParameterSetName="Path",
    SupportsShouldProcess=$true,
    HelpUri="https://go.microsoft.com/fwlink/?linkid=2096769")]
    [OutputType([System.IO.FileSystemInfo])]
    param
    (
        [parameter (
        mandatory=$true,
        Position=0,
        ParameterSetName="Path",
        ValueFromPipeline=$true,
        ValueFromPipelineByPropertyName=$true)]
        [ValidateNotNullOrEmpty()]
        [string] $Path,

        [parameter (
        mandatory=$true,
        ParameterSetName="LiteralPath",
        ValueFromPipelineByPropertyName=$true)]
        [ValidateNotNullOrEmpty()]
        [Alias("PSPath")]
        [string] $LiteralPath,

        [parameter (mandatory=$false,
        Position=1,
        ValueFromPipeline=$false,
        ValueFromPipelineByPropertyName=$false)]
        [ValidateNotNullOrEmpty()]
        [string] $DestinationPath,

        [parameter (mandatory=$false,
        ValueFromPipeline=$false,
        ValueFromPipelineByPropertyName=$false)]
        [switch] $Force,

        [switch]
        $PassThru = $false
    )

    BEGIN
    {
       $isVerbose = $psboundparameters.ContainsKey("Verbose")
       $isConfirm = $psboundparameters.ContainsKey("Confirm")

        $isDestinationPathProvided = $true
        if($DestinationPath -eq [string]::Empty)
        {
            $resolvedDestinationPath = (Get-Location).ProviderPath
            $isDestinationPathProvided = $false
        }
        else
        {
            $destinationPathExists = Test-Path -Path $DestinationPath -PathType Container
            if($destinationPathExists)
            {
                $resolvedDestinationPath = GetResolvedPathHelper $DestinationPath $false $PSCmdlet
                if($resolvedDestinationPath.Count -gt 1)
                {
                    $errorMessage = ($LocalizedData.InvalidExpandedDirPathError -f $DestinationPath)
                    ThrowTerminatingErrorHelper "InvalidDestinationPath" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $DestinationPath
                }

                # At this point we are sure that the provided path resolves to a valid single path.
                # Calling Resolve-Path again to get the underlying provider name.
                $suppliedDestinationPath = Resolve-Path -Path $DestinationPath
                if($suppliedDestinationPath.Provider.Name-ne "FileSystem")
                {
                    $errorMessage = ($LocalizedData.ExpandArchiveInValidDestinationPath -f $DestinationPath)
                    ThrowTerminatingErrorHelper "InvalidDirectoryPath" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $DestinationPath
                }
            }
            else
            {
                $createdItem = New-Item -Path $DestinationPath -ItemType Directory -Confirm:$isConfirm -Verbose:$isVerbose -ErrorAction Stop
                if($createdItem -ne $null -and $createdItem.PSProvider.Name -ne "FileSystem")
                {
                    Remove-Item "$DestinationPath" -Force -Recurse -ErrorAction SilentlyContinue
                    $errorMessage = ($LocalizedData.ExpandArchiveInValidDestinationPath -f $DestinationPath)
                    ThrowTerminatingErrorHelper "InvalidDirectoryPath" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $DestinationPath
                }

                $resolvedDestinationPath = GetResolvedPathHelper $DestinationPath $true $PSCmdlet
            }
        }

        $isWhatIf = $psboundparameters.ContainsKey("WhatIf")
        if(!$isWhatIf)
        {
            $preparingToExpandVerboseMessage = ($LocalizedData.PreparingToExpandVerboseMessage)
            Write-Verbose $preparingToExpandVerboseMessage

            $progressBarStatus = ($LocalizedData.ExpandProgressBarText -f $DestinationPath)
            Write-Progress -Activity "Expand-Archive" -Status $progressBarStatus
        }
    }
    PROCESS
    {
        switch($PsCmdlet.ParameterSetName)
        {
            "Path"
            {
                $resolvedSourcePaths = GetResolvedPathHelper $Path $false $PSCmdlet

                if($resolvedSourcePaths.Count -gt 1)
                {
                    $errorMessage = ($LocalizedData.InvalidArchiveFilePathError -f $Path, $PsCmdlet.ParameterSetName, $PsCmdlet.ParameterSetName)
                    ThrowTerminatingErrorHelper "InvalidArchiveFilePath" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $Path
                }
            }
            "LiteralPath"
            {
                $resolvedSourcePaths = GetResolvedPathHelper $LiteralPath $true $PSCmdlet

                if($resolvedSourcePaths.Count -gt 1)
                {
                    $errorMessage = ($LocalizedData.InvalidArchiveFilePathError -f $LiteralPath, $PsCmdlet.ParameterSetName, $PsCmdlet.ParameterSetName)
                    ThrowTerminatingErrorHelper "InvalidArchiveFilePath" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $LiteralPath
                }
            }
        }

        ValidateArchivePathHelper $resolvedSourcePaths

        if($pscmdlet.ShouldProcess($resolvedSourcePaths))
        {
            $expandedItems = @()

            try
            {
                # StopProcessing is not available in Script cmdlets. However the pipeline execution
                # is terminated when ever 'CTRL + C' is entered by user to terminate the cmdlet execution.
                # The finally block is executed whenever pipeline is terminated.
                # $isArchiveFileProcessingComplete variable is used to track if 'CTRL + C' is entered by the
                # user.
                $isArchiveFileProcessingComplete = $false

                # The User has not provided a destination path, hence we use '$pwd\ArchiveFileName' as the directory where the
                # archive file contents would be expanded. If the path '$pwd\ArchiveFileName' already exists then we use the
                # Windows default mechanism of appending a counter value at the end of the directory name where the contents
                # would be expanded.
                if(!$isDestinationPathProvided)
                {
                    $archiveFile = New-Object System.IO.FileInfo $resolvedSourcePaths
                    $resolvedDestinationPath = Join-Path -Path $resolvedDestinationPath -ChildPath $archiveFile.BaseName
                    $destinationPathExists = Test-Path -LiteralPath $resolvedDestinationPath -PathType Container

                    if(!$destinationPathExists)
                    {
                        New-Item -Path $resolvedDestinationPath -ItemType Directory -Confirm:$isConfirm -Verbose:$isVerbose -ErrorAction Stop | Out-Null
                    }
                }

                ExpandArchiveHelper $resolvedSourcePaths $resolvedDestinationPath ([ref]$expandedItems) $Force $isVerbose $isConfirm

                $isArchiveFileProcessingComplete = $true
            }
            finally
            {
                # The $isArchiveFileProcessingComplete would be set to $false if user has typed 'CTRL + C' to
                # terminate the cmdlet execution or if an unhandled exception is thrown.
                if($isArchiveFileProcessingComplete -eq $false)
                {
                    $failedToExpandVerboseMessage = ($LocalizedData.FailedToExpandVerboseMessage -f $resolvedDestinationPath)
                    Write-Verbose $failedToExpandVerboseMessage
                }
                elseif ($PassThru -and $expandedItems.Count -gt 0)
                {
                    # Return the expanded items, being careful to remove trailing directory separators from
                    # any folder paths for consistency
                    $trailingDirSeparators = '\' + [System.IO.Path]::DirectorySeparatorChar + '+$'
                    Get-Item -LiteralPath ($expandedItems -replace $trailingDirSeparators)
                }
            }
        }
    }
}

<############################################################################################
# GetResolvedPathHelper: This is a helper function used to resolve the user specified Path.
# The path can either be absolute or relative path.
############################################################################################>
function GetResolvedPathHelper
{
    param
    (
        [string[]] $path,
        [boolean] $isLiteralPath,
        [System.Management.Automation.PSCmdlet]
        $callerPSCmdlet
    )

    $resolvedPaths =@()

    # null and empty check are are already done on Path parameter at the cmdlet layer.
    foreach($currentPath in $path)
    {
        try
        {
            if($isLiteralPath)
            {
                $currentResolvedPaths = Resolve-Path -LiteralPath $currentPath -ErrorAction Stop
            }
            else
            {
                $currentResolvedPaths = Resolve-Path -Path $currentPath -ErrorAction Stop
            }
        }
        catch
        {
            $errorMessage = ($LocalizedData.PathNotFoundError -f $currentPath)
            $exception = New-Object System.InvalidOperationException $errorMessage, $_.Exception
            $errorRecord = CreateErrorRecordHelper "ArchiveCmdletPathNotFound" $null ([System.Management.Automation.ErrorCategory]::InvalidArgument) $exception $currentPath
            $callerPSCmdlet.ThrowTerminatingError($errorRecord)
        }

        foreach($currentResolvedPath in $currentResolvedPaths)
        {
            $resolvedPaths += $currentResolvedPath.ProviderPath
        }
    }

    $resolvedPaths
}

function Add-CompressionAssemblies {
    Add-Type -AssemblyName System.IO.Compression
    if ($psedition -eq "Core")
    {
        Add-Type -AssemblyName System.IO.Compression.ZipFile
    }
    else
    {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }
}

function IsValidFileSystemPath
{
    param
    (
        [string[]] $path
    )

    $result = $true;

    # null and empty check are are already done on Path parameter at the cmdlet layer.
    foreach($currentPath in $path)
    {
        if(!([System.IO.File]::Exists($currentPath) -or [System.IO.Directory]::Exists($currentPath)))
        {
            $errorMessage = ($LocalizedData.PathNotFoundError -f $currentPath)
            ThrowTerminatingErrorHelper "PathNotFound" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $currentPath
        }
    }

    return $result;
}


function ValidateDuplicateFileSystemPath
{
    param
    (
        [string] $inputParameter,
        [string[]] $path
    )

    $uniqueInputPaths = @()

    # null and empty check are are already done on Path parameter at the cmdlet layer.
    foreach($currentPath in $path)
    {
        $currentInputPath = $currentPath.ToUpper()
        if($uniqueInputPaths.Contains($currentInputPath))
        {
            $errorMessage = ($LocalizedData.DuplicatePathFoundError -f $inputParameter, $currentPath, $inputParameter)
            ThrowTerminatingErrorHelper "DuplicatePathFound" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $currentPath
        }
        else
        {
            $uniqueInputPaths += $currentInputPath
        }
    }
}

function CompressionLevelMapper
{
    param
    (
        [string] $compressionLevel
    )

    $compressionLevelFormat = [System.IO.Compression.CompressionLevel]::Optimal

    # CompressionLevel format is already validated at the cmdlet layer.
    switch($compressionLevel.ToString())
    {
        "Fastest"
        {
            $compressionLevelFormat = [System.IO.Compression.CompressionLevel]::Fastest
        }
        "NoCompression"
        {
            $compressionLevelFormat = [System.IO.Compression.CompressionLevel]::NoCompression
        }
    }

    return $compressionLevelFormat
}

function CompressArchiveHelper
{
    param
    (
        [string[]] $sourcePath,
        [string]   $destinationPath,
        [string]   $compressionLevel,
        [bool]     $isUpdateMode
    )

    $numberOfItemsArchived = 0
    $sourceFilePaths = @()
    $sourceDirPaths = @()

    foreach($currentPath in $sourcePath)
    {
        $result = Test-Path -LiteralPath $currentPath -Type Leaf
        if($result -eq $true)
        {
            $sourceFilePaths += $currentPath
        }
        else
        {
            $sourceDirPaths += $currentPath
        }
    }

    # The Source Path contains one or more directory (this directory can have files under it) and no files to be compressed.
    if($sourceFilePaths.Count -eq 0 -and $sourceDirPaths.Count -gt 0)
    {
        $currentSegmentWeight = 100/[double]$sourceDirPaths.Count
        $previousSegmentWeight = 0
        foreach($currentSourceDirPath in $sourceDirPaths)
        {
            $count = CompressSingleDirHelper $currentSourceDirPath $destinationPath $compressionLevel $true $isUpdateMode $previousSegmentWeight $currentSegmentWeight
            $numberOfItemsArchived += $count
            $previousSegmentWeight += $currentSegmentWeight
        }
    }

    # The Source Path contains only files to be compressed.
    elseIf($sourceFilePaths.Count -gt 0 -and $sourceDirPaths.Count -eq 0)
    {
        # $previousSegmentWeight is equal to 0 as there are no prior segments.
        # $currentSegmentWeight is set to 100 as all files have equal weightage.
        $previousSegmentWeight = 0
        $currentSegmentWeight = 100

        $numberOfItemsArchived = CompressFilesHelper $sourceFilePaths $destinationPath $compressionLevel $isUpdateMode $previousSegmentWeight $currentSegmentWeight
    }
    # The Source Path contains one or more files and one or more directories (this directory can have files under it) to be compressed.
    elseif($sourceFilePaths.Count -gt 0 -and $sourceDirPaths.Count -gt 0)
    {
        # each directory is considered as an individual segments & all the individual files are clubed in to a separate segment.
        $currentSegmentWeight = 100/[double]($sourceDirPaths.Count +1)
        $previousSegmentWeight = 0

        foreach($currentSourceDirPath in $sourceDirPaths)
        {
            $count = CompressSingleDirHelper $currentSourceDirPath $destinationPath $compressionLevel $true $isUpdateMode $previousSegmentWeight $currentSegmentWeight
            $numberOfItemsArchived += $count
            $previousSegmentWeight += $currentSegmentWeight
        }

        $count = CompressFilesHelper $sourceFilePaths $destinationPath $compressionLevel $isUpdateMode $previousSegmentWeight $currentSegmentWeight
        $numberOfItemsArchived += $count
    }

    return $numberOfItemsArchived
}

function CompressFilesHelper
{
    param
    (
        [string[]] $sourceFilePaths,
        [string]   $destinationPath,
        [string]   $compressionLevel,
        [bool]     $isUpdateMode,
        [double]   $previousSegmentWeight,
        [double]   $currentSegmentWeight
    )

    $numberOfItemsArchived = ZipArchiveHelper $sourceFilePaths $destinationPath $compressionLevel $isUpdateMode $null $previousSegmentWeight $currentSegmentWeight

    return $numberOfItemsArchived
}

function CompressSingleDirHelper
{
    param
    (
        [string] $sourceDirPath,
        [string] $destinationPath,
        [string] $compressionLevel,
        [bool]   $useParentDirAsRoot,
        [bool]   $isUpdateMode,
        [double] $previousSegmentWeight,
        [double] $currentSegmentWeight
    )

    [System.Collections.Generic.List[System.String]]$subDirFiles = @()

    if($useParentDirAsRoot)
    {
        $sourceDirInfo = New-Object -TypeName System.IO.DirectoryInfo -ArgumentList $sourceDirPath
        $sourceDirFullName = $sourceDirInfo.Parent.FullName

        # If the directory is present at the drive level the DirectoryInfo.Parent include directory separator. example: C:\
        # On the other hand if the directory exists at a deper level then DirectoryInfo.Parent
        # has just the path (without an ending directory separator). example C:\source
        if($sourceDirFullName.Length -eq 3)
        {
            $modifiedSourceDirFullName = $sourceDirFullName
        }
        else
        {
            $modifiedSourceDirFullName = $sourceDirFullName + [System.IO.Path]::DirectorySeparatorChar
        }
    }
    else
    {
        $sourceDirFullName = $sourceDirPath
        $modifiedSourceDirFullName = $sourceDirFullName + [System.IO.Path]::DirectorySeparatorChar
    }

    $dirContents = Get-ChildItem -LiteralPath $sourceDirPath -Recurse
    foreach($currentContent in $dirContents)
    {
        $isContainer = $currentContent -is [System.IO.DirectoryInfo]
        if(!$isContainer)
        {
            $subDirFiles.Add($currentContent.FullName)
        }
        else
        {
            # The currentContent points to a directory.
            # We need to check if the directory is an empty directory, if so such a
            # directory has to be explicitly added to the archive file.
            # if there are no files in the directory the GetFiles() API returns an empty array.
            $files = $currentContent.GetFiles()
            if($files.Count -eq 0)
            {
                $subDirFiles.Add($currentContent.FullName + [System.IO.Path]::DirectorySeparatorChar)
            }
        }
    }

    $numberOfItemsArchived = ZipArchiveHelper $subDirFiles.ToArray() $destinationPath $compressionLevel $isUpdateMode $modifiedSourceDirFullName $previousSegmentWeight $currentSegmentWeight

    return $numberOfItemsArchived
}

function ZipArchiveHelper
{
    param
    (
        [System.Collections.Generic.List[System.String]] $sourcePaths,
        [string]   $destinationPath,
        [string]   $compressionLevel,
        [bool]     $isUpdateMode,
        [string]   $modifiedSourceDirFullName,
        [double]   $previousSegmentWeight,
        [double]   $currentSegmentWeight
    )

    $numberOfItemsArchived = 0
    $fileMode = [System.IO.FileMode]::Create
    $result = Test-Path -LiteralPath $DestinationPath -Type Leaf
    if($result -eq $true)
    {
        $fileMode = [System.IO.FileMode]::Open
    }

    Add-CompressionAssemblies

    try
    {
        # At this point we are sure that the archive file has write access.
        $archiveFileStreamArgs = @($destinationPath, $fileMode)
        $archiveFileStream = New-Object -TypeName System.IO.FileStream -ArgumentList $archiveFileStreamArgs

        $zipArchiveArgs = @($archiveFileStream, [System.IO.Compression.ZipArchiveMode]::Update, $false)
        $zipArchive = New-Object -TypeName System.IO.Compression.ZipArchive -ArgumentList $zipArchiveArgs

        $currentEntryCount = 0
        $progressBarStatus = ($LocalizedData.CompressProgressBarText -f $destinationPath)
        $bufferSize = 4kb
        $buffer = New-Object Byte[] $bufferSize

        foreach($currentFilePath in $sourcePaths)
        {
            if($modifiedSourceDirFullName -ne $null -and $modifiedSourceDirFullName.Length -gt 0)
            {
                $index = $currentFilePath.IndexOf($modifiedSourceDirFullName, [System.StringComparison]::OrdinalIgnoreCase)
                $currentFilePathSubString = $currentFilePath.Substring($index, $modifiedSourceDirFullName.Length)
                $relativeFilePath = $currentFilePath.Replace($currentFilePathSubString, "").Trim()
            }
            else
            {
                $relativeFilePath = [System.IO.Path]::GetFileName($currentFilePath)
            }

            # Update mode is selected.
            # Check to see if archive file already contains one or more zip files in it.
            if($isUpdateMode -eq $true -and $zipArchive.Entries.Count -gt 0)
            {
                $entryToBeUpdated = $null

                # Check if the file already exists in the archive file.
                # If so replace it with new file from the input source.
                # If the file does not exist in the archive file then default to
                # create mode and create the entry in the archive file.

                foreach($currentArchiveEntry in $zipArchive.Entries)
                {
                    if(ArchivePathCompareHelper $currentArchiveEntry.FullName $relativeFilePath)
                    {
                        $entryToBeUpdated = $currentArchiveEntry
                        break
                    }
                }

                if($entryToBeUpdated -ne $null)
                {
                    $addItemtoArchiveFileMessage = ($LocalizedData.AddItemtoArchiveFile -f $currentFilePath)
                    $entryToBeUpdated.Delete()
                }
            }

            $compression = CompressionLevelMapper $compressionLevel

            # If a directory needs to be added to an archive file,
            # by convention the .Net API's expect the path of the directory
            # to end with directory separator to detect the path as an directory.
            if(!$relativeFilePath.EndsWith([System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
            {
                try
                {
                    try
                    {
                        $currentFileStream = [System.IO.File]::Open($currentFilePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
                    }
                    catch
                    {
                        # Failed to access the file. Write a non terminating error to the pipeline
                        # and move on with the remaining files.
                        $exception = $_.Exception
                        if($null -ne $_.Exception -and
                        $null -ne $_.Exception.InnerException)
                        {
                            $exception = $_.Exception.InnerException
                        }
                        $errorRecord = CreateErrorRecordHelper "CompressArchiveUnauthorizedAccessError" $null ([System.Management.Automation.ErrorCategory]::PermissionDenied) $exception $currentFilePath
                        Write-Error -ErrorRecord $errorRecord
                    }

                    if($null -ne $currentFileStream)
                    {
                        $srcStream = New-Object System.IO.BinaryReader $currentFileStream

                        $entryPath = DirectorySeparatorNormalizeHelper $relativeFilePath
                        $currentArchiveEntry = $zipArchive.CreateEntry($entryPath, $compression)

                        # Updating  the File Creation time so that the same timestamp would be retained after expanding the compressed file.
                        # At this point we are sure that Get-ChildItem would succeed.
                        $lastWriteTime = (Get-Item -LiteralPath $currentFilePath).LastWriteTime
                        if ($lastWriteTime.Year -lt 1980)
                        {
                            Write-Warning "'$currentFilePath' has LastWriteTime earlier than 1980. Compress-Archive will store any files with LastWriteTime values earlier than 1980 as 1/1/1980 00:00."
                            $lastWriteTime = [DateTime]::Parse('1980-01-01T00:00:00')
                        }

                        $currentArchiveEntry.LastWriteTime = $lastWriteTime

                        $destStream = New-Object System.IO.BinaryWriter $currentArchiveEntry.Open()

                        while($numberOfBytesRead = $srcStream.Read($buffer, 0, $bufferSize))
                        {
                            $destStream.Write($buffer, 0, $numberOfBytesRead)
                            $destStream.Flush()
                        }

                        $numberOfItemsArchived += 1
                        $addItemtoArchiveFileMessage = ($LocalizedData.AddItemtoArchiveFile -f $currentFilePath)
                    }
                }
                finally
                {
                    If($null -ne $currentFileStream)
                    {
                        $currentFileStream.Dispose()
                    }
                    If($null -ne $srcStream)
                    {
                        $srcStream.Dispose()
                    }
                    If($null -ne $destStream)
                    {
                        $destStream.Dispose()
                    }
                }
            }
            else
            {
                $entryPath = DirectorySeparatorNormalizeHelper $relativeFilePath
                $currentArchiveEntry = $zipArchive.CreateEntry($entryPath, $compression)
                $numberOfItemsArchived += 1
                $addItemtoArchiveFileMessage = ($LocalizedData.AddItemtoArchiveFile -f $currentFilePath)
            }

            if($null -ne $addItemtoArchiveFileMessage)
            {
                Write-Verbose $addItemtoArchiveFileMessage
            }

            $currentEntryCount += 1
            ProgressBarHelper "Compress-Archive" $progressBarStatus $previousSegmentWeight $currentSegmentWeight $sourcePaths.Count  $currentEntryCount
        }
    }
    finally
    {
        If($null -ne $zipArchive)
        {
            $zipArchive.Dispose()
        }

        If($null -ne $archiveFileStream)
        {
            $archiveFileStream.Dispose()
        }

        # Complete writing progress.
        Write-Progress -Activity "Compress-Archive" -Completed
    }

    return $numberOfItemsArchived
}

<############################################################################################
# ValidateArchivePathHelper: This is a helper function used to validate the archive file
# path & its file format. The only supported archive file format is .zip
############################################################################################>
function ValidateArchivePathHelper
{
    param
    (
        [string] $archiveFile
    )

    if(-not [System.IO.File]::Exists($archiveFile))
    {
        $errorMessage = ($LocalizedData.PathNotFoundError -f $archiveFile)
        ThrowTerminatingErrorHelper "PathNotFound" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidArgument) $archiveFile
    }
}

<############################################################################################
# Get-WindowsArchiveEntryPathValidationResult: Validates a raw archive entry path for Windows
# and returns whether the path should be rejected plus a potentially sanitized path.
# Invalid entries include Win32 device path prefixes (\\.\  \\?\  //./  //?/) and any
# segment containing ':'. Reserved Windows device names in segments are sanitized and prefixed with '_'.
###############################################################################################>
function Get-WindowsArchiveEntryPathValidationResult
{
    param([string] $Path)

    $result = [PSCustomObject]@{
        ContainsInvalidDevicePathPrefix = $false
        ContainsReservedDeviceName = $false
        ReservedDeviceNameUpdatedPath = $null
    }

    if ([string]::IsNullOrEmpty($Path))
    {
        return $result
    }

    # Colons are illegal in NTFS filenames (i.e NUL:, NUL:stream, file:bad) and must be rejected.
    # This method is only called with the entry name and does not contain any drive path (i.e 'C:')
    # Also validate against Win32 device path prefixes (\\.\  \\?\  //./  //?/)
    if ($Path -match '^(\\\\|//)[.?][/\\]' -or $Path.Contains(':'))
    {
        $result.ContainsInvalidDevicePathPrefix = $true
        return $result
    }

    $updatedSegments = New-Object System.Collections.Generic.List[string]

    foreach ($segment in ($Path -split '[/\\]'))
    {
        $updatedSegment = $segment
        $trimmedSegment = $segment.TrimEnd(' .')
        # Win32 strips trailing spaces and periods before resolving entries: "NUL " -> "NUL", "NUL." -> "NUL", this trimming should be preserved when validating and renaming entries.
        if ($script:reservedDeviceNames.Contains($trimmedSegment))
        {
            $updatedSegment = '_' + $trimmedSegment
            $result.ContainsReservedDeviceName = $true
        }

        $updatedSegments.Add($updatedSegment)
    }

    if ($result.ContainsReservedDeviceName)
    {
        $result.ReservedDeviceNameUpdatedPath = [string]::Join([System.IO.Path]::DirectorySeparatorChar, $updatedSegments)
    }

    return $result
}

<############################################################################################
# ExpandArchiveHelper: This is a helper function used to expand the archive file contents
# to the specified directory.
############################################################################################>
function ExpandArchiveHelper
{
    param
    (
        [string]  $archiveFile,
        [string]  $expandedDir,
        [ref]     $expandedItems,
        [boolean] $force,
        [boolean] $isVerbose,
        [boolean] $isConfirm
    )

    Add-CompressionAssemblies

    try
    {
        # The existence of archive file has already been validated by ValidateArchivePathHelper
        # before calling this helper function.
        $archiveFileStreamArgs = @($archiveFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read)
        $archiveFileStream = New-Object -TypeName System.IO.FileStream -ArgumentList $archiveFileStreamArgs

        $zipArchiveArgs = @($archiveFileStream, [System.IO.Compression.ZipArchiveMode]::Read, $false)
        try
        {
            $zipArchive = New-Object -TypeName System.IO.Compression.ZipArchive -ArgumentList $zipArchiveArgs
        }
        catch [System.IO.InvalidDataException]
        {
            # Failed to open the file for reading as a zip archive. Wrap the exception
            # and re-throw it indicating it does not appear to be a valid zip file.
            $exception = $_.Exception
            if($null -ne $_.Exception -and
               $null -ne $_.Exception.InnerException)
            {
                $exception = $_.Exception.InnerException
            }
            # Load the WindowsBase.dll assembly to get access to the System.IO.FileFormatException class
            [System.Reflection.Assembly]::Load('WindowsBase,Version=4.0.0.0,Culture=neutral,PublicKeyToken=31bf3856ad364e35')
            $invalidFileFormatException = New-Object -TypeName System.IO.FileFormatException -ArgumentList @(
                ($LocalizedData.ItemDoesNotAppearToBeAValidZipArchive -f $archiveFile)
                $exception
            )
            throw $invalidFileFormatException
        }

        if($zipArchive.Entries.Count -eq 0)
        {
            $archiveFileIsEmpty = ($LocalizedData.ArchiveFileIsEmpty -f $archiveFile)
            Write-Verbose $archiveFileIsEmpty
            return
        }

        # Ensures that the last character on the extraction path is the directory separator char.
        # Without this, a bad zip file could try to traverse outside of the expected extraction path.
        # At this point $expandedDir is a fully qualified path without any relative segments.
        if (-not $expandedDir.EndsWith([System.IO.Path]::DirectorySeparatorChar))
        {
	        $expandedDir += [System.IO.Path]::DirectorySeparatorChar
        }

        # Evaluate all entries (and write any errors), extraction will occur at the end if no errors are present.
        $hasTerminatingError = $false
        $overwriteFiles = $force

        $evaluatingArchiveEntriesMessage = ($LocalizedData.EvaluatingArchiveEntries)
        Write-Verbose $evaluatingArchiveEntriesMessage

        # The archive entries can either be empty directories or files.
        foreach($currentArchiveEntry in $zipArchive.Entries)
        {
            $entryPath = $currentArchiveEntry.FullName
            $isWindowsOS = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

            if ($isWindowsOS)
            {
                # Validate the raw entry name before any path resolution.
                $pathValidationResult = Get-WindowsArchiveEntryPathValidationResult -Path $entryPath

                if ($pathValidationResult.ContainsInvalidDevicePathPrefix)
                {
                    # Write error for invalid paths with Win32 device path prefixes (\\.\ \\?\ //. //?) or segments containing ':'
                    $ReservedDevicePathArchiveEntryMessage = ($LocalizedData.BadArchiveEntry -f $entryPath)
                    Write-Error $ReservedDevicePathArchiveEntryMessage
                    $hasTerminatingError = $true
                    continue
                }

                if ($pathValidationResult.ContainsReservedDeviceName)
                {
                    # Write error for invalid paths with Windows reserved device names.
                    $ReservedDeviceNameArchiveEntryMessage = ($LocalizedData.ReservedDeviceNameInArchiveEntry -f $entryPath, $pathValidationResult.ReservedDeviceNameUpdatedPath)
                    Write-Error $ReservedDeviceNameArchiveEntryMessage
                    $hasTerminatingError = $true
                    continue
                }
            }

            # Windows filesystem provider will internally convert from `/` to `\`
            $currentArchiveEntryPath = Join-Path -Path $expandedDir -ChildPath $entryPath

            # Remove possible relative segments from target
            # This is similar to [System.IO.Path]::GetFullPath($currentArchiveEntryPath) but uses PS current dir instead of process-wide current dir
            $currentArchiveEntryPath = $PSCmdlet.SessionState.Path.GetUnresolvedProviderPathFromPSPath($currentArchiveEntryPath)
            
            # Check that expanded relative paths and absolute paths from the archive are Not going outside of target directory
            # Ordinal match is safest, case-sensitive volumes can be mounted within volumes that are case-insensitive.
            if (-not ($currentArchiveEntryPath.StartsWith($expandedDir, [System.StringComparison]::Ordinal)))
            {
                $BadArchiveEntryMessage = ($LocalizedData.BadArchiveEntry -f $entryPath)
                # notify user of bad archive entry
                Write-Error $BadArchiveEntryMessage
                $hasTerminatingError = $true
                continue
            }

  
            $extension = [system.IO.Path]::GetExtension($currentArchiveEntryPath)

            # To keep track of each expanded file or parent directory entry that will later be created when expanding the archive file, add each entry to the $expandedItems array.
            if ($extension -ne [string]::Empty)
            {
                try
                {
                    $currentArchiveEntryFileInfo = New-Object -TypeName System.IO.FileInfo -ArgumentList $currentArchiveEntryPath
                    $parentDirExists = Test-Path -LiteralPath $currentArchiveEntryFileInfo.DirectoryName -PathType Container

                    # If the Parent directory does not exist and is not already tracked, track it for -PassThru.
                    if(($parentDirExists -eq $false) -and ($expandedItems.Value -notcontains $currentArchiveEntryFileInfo.DirectoryName))
                    {
                        $expandedItems.Value += $currentArchiveEntryFileInfo.DirectoryName
                    }

                    # Check if the file in to which the current archive entry contents would be expanded already exists.
                    if($currentArchiveEntryFileInfo.Exists)
                    {
                        if ($overwriteFiles -eq $false)
                        {
                            # Write non-terminating error to the pipeline, if overwrite is false and the file already exists.
                            $errorMessage = ($LocalizedData.FileExistsError -f $currentArchiveEntryFileInfo.FullName, $archiveFile, $currentArchiveEntryFileInfo.FullName, $currentArchiveEntryFileInfo.FullName)
                            $errorRecord = CreateErrorRecordHelper "ExpandArchiveFileExists" $errorMessage ([System.Management.Automation.ErrorCategory]::InvalidOperation) $null $currentArchiveEntryFileInfo.FullName
                            Write-Error -ErrorRecord $errorRecord
                            $hasTerminatingError = $true
                        }
                    }

                    $expandedItems.Value += $currentArchiveEntryPath # for the file/leaf
                }
                finally
                {
                    If($null -ne $destStream)
                    {
                        $destStream.Dispose()
                    }

                    If($null -ne $srcStream)
                    {
                        $srcStream.Dispose()
                    }
                }
            }
        }

        $completedEvaluatingArchiveEntriesMessage = ($LocalizedData.CompletedEvaluatingArchiveEntries)
        Write-Verbose $completedEvaluatingArchiveEntriesMessage

        # Only attempt extraction if entries were found to have no errors when validating (i.e, no Windows reserved device names, Windows device paths, etc.)
        if ($hasTerminatingError -eq $false)
        {
            Write-Progress -Activity "Expand-Archive" -Status ($LocalizedData.ExpandProgressBarBeforeExtractionText -f $archiveFile)
            [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($zipArchive, $expandedDir, $overwriteFiles)
        }
    }
    finally
    {
        Write-Progress -Activity "Expand-Archive" -Status ($LocalizedData.ExpandProgressBarAfterExtractionText -f $archiveFile)
        If($null -ne $zipArchive)
        {
            $zipArchive.Dispose()
        }

        If($null -ne $archiveFileStream)
        {
            $archiveFileStream.Dispose()
        }

        # Complete writing progress.
        Write-Progress -Activity "Expand-Archive" -Completed
    }
}

<############################################################################################
# ProgressBarHelper: This is a helper function used to display progress message.
# This function is used by both Compress-Archive & Expand-Archive to display archive file
# creation/expansion progress.
############################################################################################>
function ProgressBarHelper
{
    param
    (
        [string] $cmdletName,
        [string] $status,
        [double] $previousSegmentWeight,
        [double] $currentSegmentWeight,
        [int]    $totalNumberofEntries,
        [int]    $currentEntryCount
    )

    if($currentEntryCount -gt 0 -and
       $totalNumberofEntries -gt 0 -and
       $previousSegmentWeight -ge 0 -and
       $currentSegmentWeight -gt 0)
    {
        $entryDefaultWeight = $currentSegmentWeight/[double]$totalNumberofEntries

        $percentComplete = $previousSegmentWeight + ($entryDefaultWeight * $currentEntryCount)
        Write-Progress -Activity $cmdletName -Status $status -PercentComplete $percentComplete
    }
}

<############################################################################################
# CSVHelper: This is a helper function used to append comma after each path specified by
# the SourcePath array. This helper function is used to display all the user supplied paths
# in the WhatIf message.
############################################################################################>
function CSVHelper
{
    param
    (
        [string[]] $sourcePath
    )

    # SourcePath has already been validated by the calling function.
    if($sourcePath.Count -gt 1)
    {
        $sourcePathInCsvFormat = "`n"
        for($currentIndex=0; $currentIndex -lt $sourcePath.Count; $currentIndex++)
        {
            if($currentIndex -eq $sourcePath.Count - 1)
            {
                $sourcePathInCsvFormat += $sourcePath[$currentIndex]
            }
            else
            {
                $sourcePathInCsvFormat += $sourcePath[$currentIndex] + "`n"
            }
        }
    }
    else
    {
        $sourcePathInCsvFormat = $sourcePath
    }

    return $sourcePathInCsvFormat
}

<############################################################################################
# ThrowTerminatingErrorHelper: This is a helper function used to throw terminating error.
############################################################################################>
function ThrowTerminatingErrorHelper
{
    param
    (
        [string] $errorId,
        [string] $errorMessage,
        [System.Management.Automation.ErrorCategory] $errorCategory,
        [object] $targetObject,
        [Exception] $innerException
    )

    if($innerException -eq $null)
    {
        $exception = New-object System.IO.IOException $errorMessage
    }
    else
    {
        $exception = New-Object System.IO.IOException $errorMessage, $innerException
    }

    $exception = New-Object System.IO.IOException $errorMessage
    $errorRecord = New-Object System.Management.Automation.ErrorRecord $exception, $errorId, $errorCategory, $targetObject
    $PSCmdlet.ThrowTerminatingError($errorRecord)
}

<############################################################################################
# CreateErrorRecordHelper: This is a helper function used to create an ErrorRecord
############################################################################################>
function CreateErrorRecordHelper
{
    param
    (
        [string] $errorId,
        [string] $errorMessage,
        [System.Management.Automation.ErrorCategory] $errorCategory,
        [Exception] $exception,
        [object] $targetObject
    )

    if($null -eq $exception)
    {
        $exception = New-Object System.IO.IOException $errorMessage
    }

    $errorRecord = New-Object System.Management.Automation.ErrorRecord $exception, $errorId, $errorCategory, $targetObject
    return $errorRecord
}

<############################################################################################
# DirectorySeparatorNormalizeHelper: This is a helper function used to normalize separators
# when compressing archives, creating cross platform archives. 
# 
# The approach taken is leveraging the fact that .net on Windows all the way back to 
# Framework 1.1 specifies `\` as DirectoryPathSeparatorChar and `/` as
# AltDirectoryPathSeparatorChar, while other platforms in .net Core use `/` for
# DirectoryPathSeparatorChar and AltDirectoryPathSeparatorChar. When using a *nix platform,
# the replacements will be no-ops, while Windows will convert all `\` to `/` for the
# purposes of the ZipEntry FullName.
############################################################################################>
function DirectorySeparatorNormalizeHelper
{
    param
    (
        [string] $archivePath
    )

    if($null -eq $archivePath)
    {
        return $archivePath
    }

    return $archivePath.replace([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

<############################################################################################
# ArchivePathCompareHelper: This is a helper function used to compare with normalized 
# separators.
############################################################################################>
function ArchivePathCompareHelper
{
    param
    (
        [string] $pathArgA,
        [string] $pathArgB
    )

    $normalizedPathArgA = DirectorySeparatorNormalizeHelper $pathArgA
    $normalizedPathArgB = DirectorySeparatorNormalizeHelper $pathArgB

    return $normalizedPathArgA -eq $normalizedPathArgB
}

# SIG # Begin signature block
# MIInKAYJKoZIhvcNAQcCoIInGTCCJxUCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCBaSTV+IU1B0meG
# cDTT3riRBoZKd+3FBOIK4YYQoStTtqCCDLowggX1MIID3aADAgECAhMzAAACHU0Z
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
# 1cY2L4A7GTQG1h32HHAvfQESWP0xghnEMIIZwAIBATBuMFcxCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAmBgNVBAMTH01pY3Jv
# c29mdCBDb2RlIFNpZ25pbmcgUENBIDIwMjQCEzMAAAIdTRnITtcPV0gAAAAAAh0w
# DQYJYIZIAWUDBAIBBQCggZAwGQYJKoZIhvcNAQkDMQwGCisGAQQBgjcCAQQwLwYJ
# KoZIhvcNAQkEMSIEIP0AUt70bXB5EYJCS+dKt2P1+QDacKBsLEoWpRZomnSMMEIG
# CisGAQQBgjcCAQwxNDAyoBSAEgBNAGkAYwByAG8AcwBvAGYAdKEagBhodHRwOi8v
# d3d3Lm1pY3Jvc29mdC5jb20wDQYJKoZIhvcNAQEBBQAEggEAGwKy/0llhPDIx3hY
# 95ORBCiH+mkLGT7M4XbIM5eKJabmv+H8rlposMFK2NqquWZ1XwMM6rz82rlHY6lZ
# LXekEK2pIVmlTnrbcgUCi6ENMiaTLnsAaWaPFUwGGkclGXEn/s/QN2rF8oy0zB3X
# JR3gDi42e3jwjz69xsB8wR1AraGD17dKdBadepAVQrqIgZeqkcz72ZhZOjT87SUo
# y1yhSW1O2chu51Ld7TgbU9egu+D53d/mgOVaUQE7EHhpmW+a1xQDFNymFDhfCPLf
# EfhbydZeYp5h/lZxOq20N9P6pIeLtoZj3BtKraZtHetHFPs5Q1moiQdDbVwQM8+5
# VmCYkKGCF5QwgheQBgorBgEEAYI3AwMBMYIXgDCCF3wGCSqGSIb3DQEHAqCCF20w
# ghdpAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFSBgsqhkiG9w0BCRABBKCCAUEEggE9
# MIIBOQIBAQYKKwYBBAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCBx3iDI8LLLBJUM
# +CYdeFzIsVeZ01OOKSRkqMt3cnOjwQIGauILkOM3GBMyMDI2MTEwMjIxNTQyMy40
# NjFaMASAAgH0oIHRpIHOMIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
# Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
# cmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScw
# JQYDVQQLEx5uU2hpZWxkIFRTUyBFU046N0YwMC0wNUUwLUQ5NDcxJTAjBgNVBAMT
# HE1pY3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2WgghHqMIIHIDCCBQigAwIBAgIT
# MwAAAh6jrKRuOW98SQABAAACHjANBgkqhkiG9w0BAQsFADB8MQswCQYDVQQGEwJV
# UzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UE
# ChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGlt
# ZS1TdGFtcCBQQ0EgMjAxMDAeFw0yNjAyMTkxOTM5NDlaFw0yNzA1MTcxOTM5NDla
# MIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMH
# UmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQL
# ExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxk
# IFRTUyBFU046N0YwMC0wNUUwLUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1l
# LVN0YW1wIFNlcnZpY2UwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCl
# 0TjtbDwsR7Fe8ac6ol5s1zhtTqd2AWpchQhLp9G5mmSM23N5fyQGCQ1D06rOA3Pg
# XKF+76vXvOCs2VsLv1owj4mHEyEqiq8GJ5yC+/QNYRpZPA8e7OgekzDO6S/4vy/j
# TMYbp3rhuFiKKCzTWOQtdFcF+D0k369I7pm/E07SyNMGkuNd5lj5SJ91UqFuZfjM
# B6cQ2wh77mtiRUVdj53yjdNqj+GQl+Yaz29Bjrzn7U1ln+JpLlnb0xdGmZoIPKZb
# wBVcWtyL4uyhML7SSTmiOfWXU+g+yNl0CdoLGL8LtWHEi8FsuTPeSdSqmeMrvLaE
# mibTVTS4vQQY8NPnb6uI5y6iNV9vBFcm8LU/lDTjGTqPa7UBT4gdf5Jm3wYrfCFZ
# 4P/j5MoqT0JONca50jt4TGI90SihXaDEYqk23S0IJZ3UkUpukDRTjK713BIykffx
# yBqMeQqfO0zvWfUx7BrmUpugQcw99+DxLl2gf+uQEpRmnlbrVJ9dvW9ds4fqEPN2
# jG0QwF1PBSglNcV1SpqZKitQgBGSwu/82AKztoCHwYRHRNwzwTVe/1KNTvmqAd4U
# ges4ywOH02haagT8wYY8OdWdjKn3k052w+kmc0UC0F+iVXTGZIMxvo9iBZQoXehz
# RtWJ/VOtKvCyS3csKzN7rStWJwjSWz6dtOf0l+ytLQIDAQABo4IBSTCCAUUwHQYD
# VR0OBBYEFOYKFprqBB0JZmJcFC4cPPmeF4JkMB8GA1UdIwQYMBaAFJ+nFV0AXmJd
# g/Tl0mWnG1M1GelyMF8GA1UdHwRYMFYwVKBSoFCGTmh0dHA6Ly93d3cubWljcm9z
# b2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFRpbWUtU3RhbXAlMjBQQ0El
# MjAyMDEwKDEpLmNybDBsBggrBgEFBQcBAQRgMF4wXAYIKwYBBQUHMAKGUGh0dHA6
# Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwVGlt
# ZS1TdGFtcCUyMFBDQSUyMDIwMTAoMSkuY3J0MAwGA1UdEwEB/wQCMAAwFgYDVR0l
# AQH/BAwwCgYIKwYBBQUHAwgwDgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEBCwUA
# A4ICAQCkoZB5NnJVFb5wKejRonk518a2TBNYpKcBMtfL6BS0ARaABOMGYLlPNuhI
# 1HwmelP9hX3oq3TaEm/cDkkzNQAzDedPgoRI2R7+8poNSWvHXEAs7SZODm9x7Kql
# BkNZM9ex4XY1yNmVOAmWDjRr7jKjaiQbntf7EC4GNikxGGaVWOjfYt3Q9X0r/Ks8
# KBlbzDR9zjA/TCctR4co1WpU1ZRLFrB9bl8dRxsbnyT2qQ41E7dT12R30eIGUziE
# s5GN+26V/ovXOi20dJiM13hYWvy1NNJAhkKOlLB1ONund6ffhPdUcHWsu8V+lR0a
# akMV64HqDbLumZrCNwUofVx3xMk8F4tCYJtQxLTywc30sZAD1S2sC1959x6KixA+
# p41FLUl8g64oHy3bfYnH5xd4JOBgQoaqndGjcctxr+8EknjhKyrgAzrTcKLJbUez
# goye8brCLJ+y6PAoEjpXRkSYAU8wfQ3YWRck6ALwoV7Uin8+rpGQSbXhF6c1dTFa
# kXmChClud4IADY/t6JRkJ+06FzL+jDd8KLV8Qj77JfiuTiPIG5G/xlnGoZFcX+yy
# BtDvzZE48d+Y+HYUd/cvhH1FKl7AH+5AyotqJSFmvM/BuYRx2B20asVXilV2k2Jb
# NO3LGCz3Q+dpElzwsfJrka1N/getma7fWpowsNvoIaEQvjad8TCCB3EwggVZoAMC
# AQICEzMAAAAVxedrngKbSZkAAAAAABUwDQYJKoZIhvcNAQELBQAwgYgxCzAJBgNV
# BAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29m
# dCBSb290IENlcnRpZmljYXRlIEF1dGhvcml0eSAyMDEwMB4XDTIxMDkzMDE4MjIy
# NVoXDTMwMDkzMDE4MzIyNVowfDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hp
# bmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jw
# b3JhdGlvbjEmMCQGA1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIwMTAw
# ggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDk4aZM57RyIQt5osvXJHm9
# DtWC0/3unAcH0qlsTnXIyjVX9gF/bErg4r25PhdgM/9cT8dm95VTcVrifkpa/rg2
# Z4VGIwy1jRPPdzLAEBjoYH1qUoNEt6aORmsHFPPFdvWGUNzBRMhxXFExN6AKOG6N
# 7dcP2CZTfDlhAnrEqv1yaa8dq6z2Nr41JmTamDu6GnszrYBbfowQHJ1S/rboYiXc
# ag/PXfT+jlPP1uyFVk3v3byNpOORj7I5LFGc6XBpDco2LXCOMcg1KL3jtIckw+DJ
# j361VI/c+gVVmG1oO5pGve2krnopN6zL64NF50ZuyjLVwIYwXE8s4mKyzbnijYjk
# lqwBSru+cakXW2dg3viSkR4dPf0gz3N9QZpGdc3EXzTdEonW/aUgfX782Z5F37Zy
# L9t9X4C626p+Nuw2TPYrbqgSUei/BQOj0XOmTTd0lBw0gg/wEPK3Rxjtp+iZfD9M
# 269ewvPV2HM9Q07BMzlMjgK8QmguEOqEUUbi0b1qGFphAXPKZ6Je1yh2AuIzGHLX
# pyDwwvoSCtdjbwzJNmSLW6CmgyFdXzB0kZSU2LlQ+QuJYfM2BjUYhEfb3BvR/bLU
# HMVr9lxSUV0S2yW6r1AFemzFER1y7435UsSFF5PAPBXbGjfHCBUYP3irRbb1Hode
# 2o+eFnJpxq57t7c+auIurQIDAQABo4IB3TCCAdkwEgYJKwYBBAGCNxUBBAUCAwEA
# ATAjBgkrBgEEAYI3FQIEFgQUKqdS/mTEmr6CkTxGNSnPEP8vBO4wHQYDVR0OBBYE
# FJ+nFV0AXmJdg/Tl0mWnG1M1GelyMFwGA1UdIARVMFMwUQYMKwYBBAGCN0yDfQEB
# MEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMv
# RG9jcy9SZXBvc2l0b3J5Lmh0bTATBgNVHSUEDDAKBggrBgEFBQcDCDAZBgkrBgEE
# AYI3FAIEDB4KAFMAdQBiAEMAQTALBgNVHQ8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB
# /zAfBgNVHSMEGDAWgBTV9lbLj+iiXGJo0T2UkFvXzpoYxDBWBgNVHR8ETzBNMEug
# SaBHhkVodHRwOi8vY3JsLm1pY3Jvc29mdC5jb20vcGtpL2NybC9wcm9kdWN0cy9N
# aWNSb29DZXJBdXRfMjAxMC0wNi0yMy5jcmwwWgYIKwYBBQUHAQEETjBMMEoGCCsG
# AQUFBzAChj5odHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpL2NlcnRzL01pY1Jv
# b0NlckF1dF8yMDEwLTA2LTIzLmNydDANBgkqhkiG9w0BAQsFAAOCAgEAnVV9/Cqt
# 4SwfZwExJFvhnnJL/Klv6lwUtj5OR2R4sQaTlz0xM7U518JxNj/aZGx80HU5bbsP
# MeTCj/ts0aGUGCLu6WZnOlNN3Zi6th542DYunKmCVgADsAW+iehp4LoJ7nvfam++
# Kctu2D9IdQHZGN5tggz1bSNU5HhTdSRXud2f8449xvNo32X2pFaq95W2KFUn0CS9
# QKC/GbYSEhFdPSfgQJY4rPf5KYnDvBewVIVCs/wMnosZiefwC2qBwoEZQhlSdYo2
# wh3DYXMuLGt7bj8sCXgU6ZGyqVvfSaN0DLzskYDSPeZKPmY7T7uG+jIa2Zb0j/aR
# AfbOxnT99kxybxCrdTDFNLB62FD+CljdQDzHVG2dY3RILLFORy3BFARxv2T5JL5z
# bcqOCb2zAVdJVGTZc9d/HltEAY5aGZFrDZ+kKNxnGSgkujhLmm77IVRrakURR6nx
# t67I6IleT53S0Ex2tVdUCbFpAUR+fKFhbHP+CrvsQWY9af3LwUFJfn6Tvsv4O+S3
# Fb+0zj6lMVGEvL8CwYKiexcdFYmNcP7ntdAoGokLjzbaukz5m/8K6TT4JDVnK+AN
# uOaMmdbhIurwJ0I9JZTmdHRbatGePu1+oDEzfbzL6Xu/OHBE0ZDxyKs6ijoIYn/Z
# cGNTTY3ugm2lBRDBcQZqELQdVTNYs6FwZvKhggNNMIICNQIBATCB+aGB0aSBzjCB
# yzELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1Jl
# ZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMc
# TWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBU
# U1MgRVNOOjdGMDAtMDVFMC1EOTQ3MSUwIwYDVQQDExxNaWNyb3NvZnQgVGltZS1T
# dGFtcCBTZXJ2aWNloiMKAQEwBwYFKw4DAhoDFQCD/QNkKDIW4VIF7j3oi2qbrR0a
# /6CBgzCBgKR+MHwxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
# DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24x
# JjAkBgNVBAMTHU1pY3Jvc29mdCBUaW1lLVN0YW1wIFBDQSAyMDEwMA0GCSqGSIb3
# DQEBCwUAAgUA7pMgtjAiGA8yMDI2MTEwMjE0NDYxNFoYDzIwMjYxMTAzMTQ0NjE0
# WjB0MDoGCisGAQQBhFkKBAExLDAqMAoCBQDukyC2AgEAMAcCAQACAgy7MAcCAQAC
# AhMSMAoCBQDulHI2AgEAMDYGCisGAQQBhFkKBAIxKDAmMAwGCisGAQQBhFkKAwKg
# CjAIAgEAAgMHoSChCjAIAgEAAgMBhqAwDQYJKoZIhvcNAQELBQADggEBADIZ4C5D
# hScu6XkB8mbju78BLVNdTfyL/aAiW8ogcCKKDjkRBMdvYmtR4kqR0muw9mNsxumd
# fFvDs0slHKxRuo+op0yD2KEfCIVTdfo9ipdBso+F0+jWZyYW2D4q+u6SW/73rIjS
# MTZou4kx3F+5Jw3NheWa6icrstzciv5Zl39jHL5I7IlkeJLt3RXNZHmdlIKyz0Ey
# DNh1rH+WKQ3wHI/0UbedlxfCoLLr2Rrh2N4j0/glNjMT+sLwN+f1t0MbqwKZXkfu
# o5vSndw1GbRj882lsc4Tk+LLlKIHWemaFJAGn6uzhKdxOZdB8tvxwNhBU4+pvOKy
# 4cu9KgiUKi26fAwxggQNMIIECQIBATCBkzB8MQswCQYDVQQGEwJVUzETMBEGA1UE
# CBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9z
# b2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQ
# Q0EgMjAxMAITMwAAAh6jrKRuOW98SQABAAACHjANBglghkgBZQMEAgEFAKCCAUow
# GgYJKoZIhvcNAQkDMQ0GCyqGSIb3DQEJEAEEMC8GCSqGSIb3DQEJBDEiBCBRUBbQ
# Iopht/ZM5RpxL91H5LLxI+m49N3rh3z6FVz1YzCB+gYLKoZIhvcNAQkQAi8xgeow
# gecwgeQwgb0EIC+BXWrz9geMgM8Bvn8bqxHjhHXJ29EBizITIw0B9vOCMIGYMIGA
# pH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcT
# B1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQGA1UE
# AxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIwMTACEzMAAAIeo6ykbjlvfEkA
# AQAAAh4wIgQg1a01C4diM2dQs1Vrt7A8E2cic25CcYUh3rKHmp/aWA0wDQYJKoZI
# hvcNAQELBQAEggIAEigpgNYPYsJOHa2fCKUqgerXpL1AL965Lfte07peL1PQMcIS
# CrDWLsZ5RnI+OYtUIPfHzJS2IssqlouF3DokFdoNxQaDsjYZarWVo8xHEhqxWeVD
# PMWAybXu36K0elTW7tbisqDJ64NQNUuEWiWu2x+MXwyb+VL3+6RVHiejWkyzjNgj
# l+WM8vWXBXI9D1avpTY43QhCbR2Yz4nthNGzBAIYFXmNP8sFqS3qcrl7l8Olt81j
# 3xk7WBOxFxitpaPb9RJi+ntbSV9jUpVkWKV9C6nov4RV/0OsTludYVkDJcOVd8Ko
# GRcq7Q+dzgsCWzlAuLDS0rlMeb40fpB+v006XzJ18AAyw6RFe+acGoMlLHEVcA31
# GqvkOaYabJ8AQECgiInZ17cBAUcOKWqcvENIj80Lpv5U5VtlFdYFaCObjEUOE9J0
# EVSu+8rTU4RZjb0mDmIlgakzOURnFMtH76bLe1hHqZX8kKprzFoTbwKhZgdICVCb
# Qr64fSkqsjfWPlaxWaloRKaQO0n5TVwDQ5HNPxCMkcjTpWEOsZNzyALibJU5R60E
# iyfGZJaumC9Z2Y4h5GPlPiSS0RCrCzj7Ak2mWGCC1twS4SIxJv2qCIR45wR4434e
# FOJkVpXu7fkO7HTjoQMKBkxnv9uOmJxaiY92FmkF4nXGyp6Tht4PAKuCsuY=
# SIG # End signature block
