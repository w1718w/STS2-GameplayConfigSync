Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceParent = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '..'))
$uploaderExe = Join-Path $workspaceParent 'tools\sts2-mod-uploader\ModUploader.exe'
$uploadRoot = Join-Path $workspaceParent 'workshop-upload\GameplayConfigSync'
$contentRoot = Join-Path $uploadRoot 'content'
$settingsPath = Join-Path $uploadRoot 'gui-settings.json'
$workshopItemId = '3803257311'
$baseLibItemId = '3737335127'
$manifestPath = Join-Path $repoRoot 'GameplayConfigSync.json'
$projectPath = Join-Path $repoRoot 'GameplayConfigSync.csproj'
$distRoot = Join-Path $repoRoot 'dist\GameplayConfigSync'
$defaultPreviewPath = Join-Path (Split-Path $uploaderExe -Parent) 'template\image.png'
$officialTags = @(
    'QoL', 'Utility', 'Misc', 'Tools & APIs', 'Cosmetics', 'Characters',
    'Cards', 'Relics', 'Potions', 'Events', 'Enemies', 'Bosses',
    'Ancients', 'Expansion'
)

function Add-Log([string]$message) {
    $logBox.AppendText("$message`r`n")
    $logBox.SelectionStart = $logBox.TextLength
    $logBox.ScrollToCaret()
    [System.Windows.Forms.Application]::DoEvents()
}

function Invoke-LoggedCommand([string]$filePath, [string[]]$arguments, [string]$workingDirectory) {
    Add-Log ("> {0} {1}" -f $filePath, ($arguments -join ' '))
    Push-Location -LiteralPath $workingDirectory
    try {
        $output = & $filePath @arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($output) { Add-Log (($output | Out-String).TrimEnd()) }
    if ($exitCode -ne 0) { throw "Command failed with exit code $exitCode" }
}

function Get-SelectedTags {
    $tags = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $tagList.CheckedItems) { $tags.Add([string]$item) }
    foreach ($item in ($customTagsBox.Text -split '[,;\r\n，；]')) {
        $tag = $item.Trim()
        if ($tag -and -not $tags.Contains($tag)) { $tags.Add($tag) }
    }
    return $tags.ToArray()
}

function Save-GuiSettings([string[]]$tags) {
    New-Item -ItemType Directory -Path $uploadRoot -Force | Out-Null
    [ordered]@{
        previewPath = $previewBox.Text
        existingFilesRoot = $existingFilesBox.Text
        visibility = $visibilityBox.SelectedItem
        includeBaseLib = $baseLibCheck.Checked
        tags = $tags
        customTags = $customTagsBox.Text
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
}

function Prepare-Upload([bool]$performUpload, [bool]$buildFirst) {
    try {
        $uploadButton.Enabled = $false
        $prepareButton.Enabled = $false
        $existingUploadButton.Enabled = $false
        $logBox.Clear()

        if (-not (Test-Path -LiteralPath $uploaderExe -PathType Leaf)) {
            throw "Official ModUploader.exe was not found: $uploaderExe"
        }
        if ([string]::IsNullOrWhiteSpace($changeNoteBox.Text)) {
            throw '请填写本次更新说明。'
        }

        if ($buildFirst) {
            Invoke-LoggedCommand 'dotnet' @('build', $projectPath, '-c', 'Release') $repoRoot
            $sourceRoot = $distRoot
        }
        else {
            $sourceRoot = $existingFilesBox.Text.Trim()
            if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
                throw '请选择包含 GitHub Release 下载文件的文件夹。'
            }
        }

        $dllPath = Join-Path $sourceRoot 'GameplayConfigSync.dll'
        $jsonPath = Join-Path $sourceRoot 'GameplayConfigSync.json'
        if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $jsonPath -PathType Leaf)) {
            throw '所选目录中必须同时存在 GameplayConfigSync.dll 和 GameplayConfigSync.json。'
        }

        $manifest = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
        if ($manifest.id -ne 'GameplayConfigSync') { throw '所选 manifest 不属于 GameplayConfigSync。' }
        Add-Log ("准备 GameplayConfigSync {0}，Workshop Item {1}" -f $manifest.version, $workshopItemId)

        New-Item -ItemType Directory -Path $contentRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $contentRoot -Force | Remove-Item -Recurse -Force
        Copy-Item -LiteralPath $dllPath -Destination $contentRoot
        Copy-Item -LiteralPath $jsonPath -Destination $contentRoot
        $cachedPreviewPath = Join-Path $uploadRoot 'image.png'
        if (-not [string]::IsNullOrWhiteSpace($previewBox.Text)) {
            if (-not (Test-Path -LiteralPath $previewBox.Text -PathType Leaf)) { throw '所选封面图不存在。' }
            if ([System.IO.Path]::GetExtension($previewBox.Text) -ne '.png') { throw '封面图必须是 PNG 文件。' }
            if ((Get-Item -LiteralPath $previewBox.Text).Length -ge 1MB) { throw '封面图必须小于 1 MB。' }
            Copy-Item -LiteralPath $previewBox.Text -Destination $cachedPreviewPath -Force
            Add-Log '使用选择的封面图。'
        }
        elseif (Test-Path -LiteralPath $cachedPreviewPath -PathType Leaf) {
            Add-Log '封面留空：复用上次上传的封面图。'
        }
        elseif (Test-Path -LiteralPath $defaultPreviewPath -PathType Leaf) {
            Copy-Item -LiteralPath $defaultPreviewPath -Destination $cachedPreviewPath -Force
            Add-Log '封面留空且无缓存：使用 Mega Crit 上传器的默认图。'
        }
        else { throw '找不到已缓存封面或 Mega Crit 默认封面。' }
        Set-Content -LiteralPath (Join-Path $uploadRoot 'mod_id.txt') -Value $workshopItemId -Encoding ASCII

        $tags = @(Get-SelectedTags)
        $visibility = if ($visibilityBox.SelectedIndex -eq 0) { $null } else { [string]$visibilityBox.SelectedItem }
        $dependencies = if ($baseLibCheck.Checked) { @($baseLibItemId) } else { $null }
        [ordered]@{
            title = $null
            description = $null
            visibility = $visibility
            changeNote = $changeNoteBox.Text.Trim()
            tags = $tags
            dependencies = $dependencies
            contentDescriptors = $null
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $uploadRoot 'workshop.json') -Encoding UTF8
        Save-GuiSettings $tags

        Add-Log ("已准备文件：{0}" -f $uploadRoot)
        if ($performUpload) {
            Invoke-LoggedCommand $uploaderExe @('upload', '--id', $workshopItemId, '--workspace', $uploadRoot) (Split-Path $uploaderExe -Parent)
            Add-Log '上传器已成功返回。Steam 会继续管理你已订阅的工坊版本。'
            [System.Windows.Forms.MessageBox]::Show('上传完成。', '创意工坊上传', 'OK', 'Information') | Out-Null
        }
    }
    catch {
        Add-Log ("错误：{0}" -f $_.Exception.Message)
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '上传未完成', 'OK', 'Error') | Out-Null
    }
    finally {
        $uploadButton.Enabled = $true
        $prepareButton.Enabled = $true
        $existingUploadButton.Enabled = $true
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$saved = $null
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    try { $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch { $saved = $null }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'GameplayConfigSync · STS2 创意工坊上传'
$form.Size = New-Object System.Drawing.Size(780, 790)
$form.StartPosition = 'CenterScreen'
$form.MinimumSize = New-Object System.Drawing.Size(780, 790)
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

$itemLabel = New-Object System.Windows.Forms.Label
$itemLabel.Location = New-Object System.Drawing.Point(18, 16)
$itemLabel.Size = New-Object System.Drawing.Size(735, 24)
$itemLabel.Text = "Item: $workshopItemId    版本: $($manifest.version)    底层: Mega Crit Mod Uploader v0.2.0"
$form.Controls.Add($itemLabel)

$noteLabel = New-Object System.Windows.Forms.Label
$noteLabel.Location = New-Object System.Drawing.Point(18, 48)
$noteLabel.Size = New-Object System.Drawing.Size(120, 22)
$noteLabel.Text = '本次更新说明'
$form.Controls.Add($noteLabel)

$changeNoteBox = New-Object System.Windows.Forms.TextBox
$changeNoteBox.Location = New-Object System.Drawing.Point(18, 72)
$changeNoteBox.Size = New-Object System.Drawing.Size(735, 90)
$changeNoteBox.Multiline = $true
$changeNoteBox.ScrollBars = 'Vertical'
$changeNoteBox.Text = "v$($manifest.version): "
$form.Controls.Add($changeNoteBox)

$tagLabel = New-Object System.Windows.Forms.Label
$tagLabel.Location = New-Object System.Drawing.Point(18, 174)
$tagLabel.Size = New-Object System.Drawing.Size(350, 22)
$tagLabel.Text = '工坊标签（常用标签勾选，其他标签在右侧输入）'
$form.Controls.Add($tagLabel)

$tagList = New-Object System.Windows.Forms.CheckedListBox
$tagList.Location = New-Object System.Drawing.Point(18, 198)
$tagList.Size = New-Object System.Drawing.Size(350, 150)
$tagList.CheckOnClick = $true
$tagList.MultiColumn = $true
$tagList.ColumnWidth = 165
foreach ($tag in $officialTags) { [void]$tagList.Items.Add($tag) }
$form.Controls.Add($tagList)

$customLabel = New-Object System.Windows.Forms.Label
$customLabel.Location = New-Object System.Drawing.Point(390, 174)
$customLabel.Size = New-Object System.Drawing.Size(360, 22)
$customLabel.Text = '自定义标签（逗号、分号或换行分隔）'
$form.Controls.Add($customLabel)

$customTagsBox = New-Object System.Windows.Forms.TextBox
$customTagsBox.Location = New-Object System.Drawing.Point(390, 198)
$customTagsBox.Size = New-Object System.Drawing.Size(363, 72)
$customTagsBox.Multiline = $true
$form.Controls.Add($customTagsBox)

$visibilityLabel = New-Object System.Windows.Forms.Label
$visibilityLabel.Location = New-Object System.Drawing.Point(390, 284)
$visibilityLabel.Size = New-Object System.Drawing.Size(75, 24)
$visibilityLabel.Text = '可见性'
$form.Controls.Add($visibilityLabel)

$visibilityBox = New-Object System.Windows.Forms.ComboBox
$visibilityBox.Location = New-Object System.Drawing.Point(466, 281)
$visibilityBox.Size = New-Object System.Drawing.Size(287, 26)
$visibilityBox.DropDownStyle = 'DropDownList'
[void]$visibilityBox.Items.AddRange(@('保持不变', 'private', 'friends_only', 'unlisted', 'public'))
$visibilityBox.SelectedIndex = 0
$form.Controls.Add($visibilityBox)

$baseLibCheck = New-Object System.Windows.Forms.CheckBox
$baseLibCheck.Location = New-Object System.Drawing.Point(390, 320)
$baseLibCheck.Size = New-Object System.Drawing.Size(350, 24)
$baseLibCheck.Text = '将 BaseLib 设为工坊依赖'
$baseLibCheck.Checked = $true
$form.Controls.Add($baseLibCheck)

$previewLabel = New-Object System.Windows.Forms.Label
$previewLabel.Location = New-Object System.Drawing.Point(18, 362)
$previewLabel.Size = New-Object System.Drawing.Size(160, 22)
$previewLabel.Text = 'PNG 封面图（可留空）'
$form.Controls.Add($previewLabel)

$previewBox = New-Object System.Windows.Forms.TextBox
$previewBox.Location = New-Object System.Drawing.Point(18, 386)
$previewBox.Size = New-Object System.Drawing.Size(650, 26)
$form.Controls.Add($previewBox)

$browseButton = New-Object System.Windows.Forms.Button
$browseButton.Location = New-Object System.Drawing.Point(678, 384)
$browseButton.Size = New-Object System.Drawing.Size(75, 29)
$browseButton.Text = '选择…'
$browseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Filter = 'PNG 图片 (*.png)|*.png'
    if ($dialog.ShowDialog() -eq 'OK') { $previewBox.Text = $dialog.FileName }
})
$form.Controls.Add($browseButton)

$existingFilesLabel = New-Object System.Windows.Forms.Label
$existingFilesLabel.Location = New-Object System.Drawing.Point(18, 422)
$existingFilesLabel.Size = New-Object System.Drawing.Size(320, 22)
$existingFilesLabel.Text = '已有发布文件目录（GitHub Release 下载件）'
$form.Controls.Add($existingFilesLabel)

$existingFilesBox = New-Object System.Windows.Forms.TextBox
$existingFilesBox.Location = New-Object System.Drawing.Point(18, 446)
$existingFilesBox.Size = New-Object System.Drawing.Size(650, 26)
$form.Controls.Add($existingFilesBox)

$existingFilesBrowseButton = New-Object System.Windows.Forms.Button
$existingFilesBrowseButton.Location = New-Object System.Drawing.Point(678, 444)
$existingFilesBrowseButton.Size = New-Object System.Drawing.Size(75, 29)
$existingFilesBrowseButton.Text = '选择…'
$existingFilesBrowseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择同时包含 GameplayConfigSync.dll 和 GameplayConfigSync.json 的目录'
    if ($dialog.ShowDialog() -eq 'OK') { $existingFilesBox.Text = $dialog.SelectedPath }
})
$form.Controls.Add($existingFilesBrowseButton)

$prepareButton = New-Object System.Windows.Forms.Button
$prepareButton.Location = New-Object System.Drawing.Point(18, 488)
$prepareButton.Size = New-Object System.Drawing.Size(155, 34)
$prepareButton.Text = '仅编译并准备文件'
$prepareButton.Add_Click({ Prepare-Upload $false $true })
$form.Controls.Add($prepareButton)

$uploadButton = New-Object System.Windows.Forms.Button
$uploadButton.Location = New-Object System.Drawing.Point(181, 488)
$uploadButton.Size = New-Object System.Drawing.Size(165, 34)
$uploadButton.Text = '编译并更新创意工坊'
$uploadButton.Add_Click({ Prepare-Upload $true $true })
$form.Controls.Add($uploadButton)

$existingUploadButton = New-Object System.Windows.Forms.Button
$existingUploadButton.Location = New-Object System.Drawing.Point(354, 488)
$existingUploadButton.Size = New-Object System.Drawing.Size(190, 34)
$existingUploadButton.Text = '上传已有发布文件'
$existingUploadButton.Add_Click({ Prepare-Upload $true $false })
$form.Controls.Add($existingUploadButton)

$openButton = New-Object System.Windows.Forms.Button
$openButton.Location = New-Object System.Drawing.Point(552, 488)
$openButton.Size = New-Object System.Drawing.Size(145, 34)
$openButton.Text = '打开工坊页面'
$openButton.Add_Click({ Start-Process "https://steamcommunity.com/sharedfiles/filedetails/?id=$workshopItemId" })
$form.Controls.Add($openButton)

$warningLabel = New-Object System.Windows.Forms.Label
$warningLabel.Location = New-Object System.Drawing.Point(18, 532)
$warningLabel.Size = New-Object System.Drawing.Size(735, 38)
$warningLabel.Text = '标题和详细描述保持不变。封面留空时复用缓存，首次则用官方默认图。始终更新现有 Item。'
$form.Controls.Add($warningLabel)

$logBox = New-Object System.Windows.Forms.TextBox
$logBox.Location = New-Object System.Drawing.Point(18, 576)
$logBox.Size = New-Object System.Drawing.Size(735, 150)
$logBox.Multiline = $true
$logBox.ReadOnly = $true
$logBox.ScrollBars = 'Both'
$logBox.WordWrap = $false
$logBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$form.Controls.Add($logBox)

if ($saved) {
    $previewBox.Text = [string]$saved.previewPath
    $existingFilesBox.Text = [string]$saved.existingFilesRoot
    $customTagsBox.Text = [string]$saved.customTags
    $baseLibCheck.Checked = [bool]$saved.includeBaseLib
    $savedVisibility = [string]$saved.visibility
    $visibilityIndex = $visibilityBox.Items.IndexOf($savedVisibility)
    if ($visibilityIndex -ge 0) { $visibilityBox.SelectedIndex = $visibilityIndex }
    foreach ($tag in @($saved.tags)) {
        $index = $tagList.Items.IndexOf([string]$tag)
        if ($index -ge 0) { $tagList.SetItemChecked($index, $true) }
    }
}
else {
    $tagList.SetItemChecked($tagList.Items.IndexOf('QoL'), $true)
    $tagList.SetItemChecked($tagList.Items.IndexOf('Utility'), $true)
}

[void]$form.ShowDialog()
