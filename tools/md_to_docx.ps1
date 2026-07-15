# 通用 Markdown → Word(.docx) 生成脚本。
# 用法：powershell -ExecutionPolicy Bypass -File tools/md_to_docx.ps1 -InputMd docs/xxx.md -OutputDocx docs/xxx.docx [-Subtitle "生成日期：自动"]
#
# 支持的 Markdown 子集（够写技术文档用）：
#   # 标题                → 文档大标题（只取第一个）
#   ## / ### / #### 标题  → 三级小标题（依次减小字号）
#   - 一级列表 / "  - " 二级列表 / "1. " 数字列表
#   > 引用                → 灰色斜体小字（用于文档说明/前言）
#   | a | b |  表格        → 转成真正的 Word 表格（第二行如果是 |---|---| 分隔线会被跳过）
#   `code`                → 等宽字体（标识符/字段名/方法名）
#   **bold**              → 加粗
#   ![说明文字](图片路径)  → 嵌入图片（居中，按页面宽度等比缩小），说明文字变成图下方的灰色小字说明
#   ---                   → 跳过（纯分隔线，不渲染）
#   普通段落
#
# 原理和 update_changelog_docx.ps1 一样：docx 本质是个 zip 包，只重新生成 word/document.xml，
# 其余文件（样式/字体/主题）直接复用 tools/docx_template 这个空白模板。
# 之所以自己手工拼 XML 而不用现成的 Word 处理库：这样这个脚本本身零依赖，只用 PowerShell 自带的
# .NET 类库就能跑，不用额外装 Word/Office 或者 Node/Python 那一整套环境，运维机器上能直接用。
param(
    [Parameter(Mandatory = $true)] [string]$InputMd,
    [Parameter(Mandatory = $true)] [string]$OutputDocx,
    [string]$Subtitle = ""
)
$ErrorActionPreference = "Stop"

$root        = Split-Path -Parent $PSScriptRoot
$mdPath      = if ([System.IO.Path]::IsPathRooted($InputMd))    { $InputMd }    else { Join-Path $root $InputMd }
$outDocx     = if ([System.IO.Path]::IsPathRooted($OutputDocx)) { $OutputDocx } else { Join-Path $root $OutputDocx }
$templateDir = Join-Path $PSScriptRoot "docx_template"

if (-not (Test-Path $mdPath))      { throw "找不到 $mdPath" }
if (-not (Test-Path $templateDir)) { throw "找不到模板目录 $templateDir" }

# 显式指定 UTF8：md 文件如果没带 BOM（文件开头几个不可见的编码标记字节），Windows 上不指定编码
# 的话可能会被当成系统默认的 GBK 去读，中文会变乱码；指定了 UTF8，不管文件带不带 BOM 都能读对。
$lines  = [System.IO.File]::ReadAllLines($mdPath, [System.Text.Encoding]::UTF8)
$mdDir  = Split-Path -Parent $mdPath

Add-Type -AssemblyName System.Drawing
# 图片嵌入需要往 docx 里加"关系"（哪个 rId 对应哪个图片文件）和媒体文件，这两样都是解析完全部段落之后
# 才统一处理（先收集用到了哪些图片，最后一次性写进 word/media 和 rels，比边解析边改包文件简单可靠）。
$imageRels = New-Object System.Collections.Generic.List[hashtable]

function Escape-Xml([string]$s) {
    $s = $s -replace '&', '&amp;'
    $s = $s -replace '<', '&lt;'
    $s = $s -replace '>', '&gt;'
    return $s
}

# 把一段文本按 **粗体** 和 `code` 拆成 (text, bold, mono) 片段列表
function Split-InlineRuns([string]$text) {
    $parts = New-Object System.Collections.ArrayList
    $regex = [regex]'\*\*(.+?)\*\*|`(.+?)`'
    $lastEnd = 0
    foreach ($m in $regex.Matches($text)) {
        if ($m.Index -gt $lastEnd) {
            [void]$parts.Add(@{ text = $text.Substring($lastEnd, $m.Index - $lastEnd); bold = $false; mono = $false })
        }
        if ($m.Groups[1].Success) { [void]$parts.Add(@{ text = $m.Groups[1].Value; bold = $true;  mono = $false }) }
        else                      { [void]$parts.Add(@{ text = $m.Groups[2].Value; bold = $false; mono = $true  }) }
        $lastEnd = $m.Index + $m.Length
    }
    if ($lastEnd -lt $text.Length) {
        [void]$parts.Add(@{ text = $text.Substring($lastEnd); bold = $false; mono = $false })
    }
    if ($parts.Count -eq 0) { [void]$parts.Add(@{ text = $text; bold = $false; mono = $false }) }
    return $parts
}

function New-Run([string]$text, [bool]$bold, [bool]$mono, [string]$color, [int]$sz) {
    $font = if ($mono) { "Consolas" } else { "微软雅黑" }
    $rPr  = "<w:rPr><w:rFonts w:ascii=`"$font`" w:hAnsi=`"$font`" w:eastAsia=`"$font`"/>"
    if ($bold)  { $rPr += "<w:b/>" }
    if ($color) { $rPr += "<w:color w:val=`"$color`"/>" }
    if ($sz -gt 0) { $rPr += "<w:sz w:val=`"$sz`"/>" }
    $rPr += "<w:lang w:eastAsia=`"zh-CN`"/></w:rPr>"
    $t = Escape-Xml $text
    $preserve = ""
    if ($t -match '^\s' -or $t -match '\s$') { $preserve = ' xml:space="preserve"' }
    return "<w:r>$rPr<w:t$preserve>$t</w:t></w:r>"
}

function Build-InlineRunsXml([string]$text, [string]$forceColor) {
    $xml = ""
    foreach ($part in (Split-InlineRuns $text)) {
        $xml += New-Run -text $part.text -bold $part.bold -mono $part.mono -color $forceColor -sz 0
    }
    return $xml
}

function New-BodyParagraph([string]$text, [int]$indent, [string]$prefix) {
    $runsXml = ""
    if ($prefix) { $runsXml += New-Run -text $prefix -bold $false -mono $false -color $null -sz 0 }
    $runsXml += Build-InlineRunsXml $text $null
    $pPr = "<w:ind w:left=`"$indent`"/><w:spacing w:after=`"80`"/>"
    return "<w:p><w:pPr>$pPr<w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>$runsXml</w:p>"
}

function New-QuoteParagraph([string]$text) {
    $rPr = "<w:rPr><w:rFonts w:ascii=`"微软雅黑`" w:hAnsi=`"微软雅黑`" w:eastAsia=`"微软雅黑`"/><w:i/><w:color w:val=`"595959`"/><w:sz w:val=`"20`"/><w:lang w:eastAsia=`"zh-CN`"/></w:rPr>"
    $t = Escape-Xml $text
    $pPr = "<w:pPr><w:spacing w:after=`"120`"/>$rPr</w:pPr>"
    return "<w:p>$pPr<w:r>$rPr<w:t xml:space=`"preserve`">$t</w:t></w:r></w:p>"
}

# 把一张图片嵌入成一个居中段落，超过页面可用宽度就按比例缩小（不会缩小到比原图还大）。
# 找不到图片文件时不让整个脚本崩掉，改成插入一行提示文字，方便发现"图片路径写错了"这种问题。
function New-ImageParagraph([string]$srcPath, [string]$caption) {
    $resolved = if ([System.IO.Path]::IsPathRooted($srcPath)) { $srcPath } else { Join-Path $mdDir $srcPath }
    if (-not (Test-Path $resolved)) {
        return (New-BodyParagraph -text "⚠ 找不到图片：$srcPath" -indent 0 -prefix $null)
    }

    $img = [System.Drawing.Image]::FromFile($resolved)
    $pxW = $img.Width; $pxH = $img.Height
    $img.Dispose()

    $maxWidthEmu = 5731510   # 页面可用宽度（9026 twips）换算成 EMU，和 New-Table 的 $pageWidth 是同一个基准
    $emuW = [long]($pxW * 9525)   # 96 DPI 截图：1 像素 = 9525 EMU
    $emuH = [long]($pxH * 9525)
    if ($emuW -gt $maxWidthEmu) {
        $ratio = $maxWidthEmu / $emuW
        $emuW  = $maxWidthEmu
        $emuH  = [long]($emuH * $ratio)
    }

    $rId      = "rIdImg" + ($imageRels.Count + 1)
    $ext      = [System.IO.Path]::GetExtension($resolved).TrimStart('.').ToLowerInvariant()
    $mediaName = "image$($imageRels.Count + 1).$ext"
    [void]$imageRels.Add(@{ RId = $rId; FileName = $mediaName; SourcePath = $resolved; Ext = $ext })

    $picId = $imageRels.Count
    $drawingXml = "<w:p><w:pPr><w:jc w:val=`"center`"/></w:pPr><w:r><w:drawing>" +
        "<wp:inline distT=`"0`" distB=`"0`" distL=`"0`" distR=`"0`">" +
        "<wp:extent cx=`"$emuW`" cy=`"$emuH`"/><wp:effectExtent l=`"0`" t=`"0`" r=`"0`" b=`"0`"/>" +
        "<wp:docPr id=`"$picId`" name=`"Picture $picId`"/>" +
        "<wp:cNvGraphicFramePr><a:graphicFrameLocks xmlns:a=`"http://schemas.openxmlformats.org/drawingml/2006/main`" noChangeAspect=`"1`"/></wp:cNvGraphicFramePr>" +
        "<a:graphic xmlns:a=`"http://schemas.openxmlformats.org/drawingml/2006/main`">" +
        "<a:graphicData uri=`"http://schemas.openxmlformats.org/drawingml/2006/picture`">" +
        "<pic:pic xmlns:pic=`"http://schemas.openxmlformats.org/drawingml/2006/picture`">" +
        "<pic:nvPicPr><pic:cNvPr id=`"$picId`" name=`"Picture $picId`"/><pic:cNvPicPr/></pic:nvPicPr>" +
        "<pic:blipFill><a:blip r:embed=`"$rId`"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
        "<pic:spPr><a:xfrm><a:off x=`"0`" y=`"0`"/><a:ext cx=`"$emuW`" cy=`"$emuH`"/></a:xfrm><a:prstGeom prst=`"rect`"><a:avLst/></a:prstGeom></pic:spPr>" +
        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>"

    $captionXml = ""
    if ($caption) {
        $rPr = "<w:rPr><w:rFonts w:ascii=`"微软雅黑`" w:hAnsi=`"微软雅黑`" w:eastAsia=`"微软雅黑`"/><w:i/><w:color w:val=`"595959`"/><w:sz w:val=`"18`"/><w:lang w:eastAsia=`"zh-CN`"/></w:rPr>"
        $t = Escape-Xml $caption
        $captionXml = "<w:p><w:pPr><w:jc w:val=`"center`"/><w:spacing w:after=`"160`"/>$rPr</w:pPr><w:r>$rPr<w:t>$t</w:t></w:r></w:p>"
    }
    return $drawingXml + $captionXml
}

function New-HeadingParagraph([string]$text, [int]$sz, [int]$before, [int]$after, [bool]$withBorder) {
    $pPr = "<w:spacing w:before=`"$before`" w:after=`"$after`"/>"
    if ($withBorder) { $pPr += "<w:pBdr><w:bottom w:val=`"single`" w:sz=`"6`" w:space=`"4`" w:color=`"1F4E79`"/></w:pBdr>" }
    $run = New-Run -text $text -bold $true -mono $false -color "1F4E79" -sz $sz
    return "<w:p><w:pPr>$pPr<w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>$run</w:p>"
}

# 把一批表格行（含表头/分隔线/数据行）转成一个 <w:tbl>
function New-Table([string[]]$rowLines) {
    $rows = New-Object System.Collections.ArrayList
    foreach ($line in $rowLines) {
        $trimmed = $line.Trim().Trim('|')
        $cells = $trimmed -split '\|' | ForEach-Object { $_.Trim() }
        [void]$rows.Add($cells)
    }
    # 第二行如果全是 --- / :--- / ---: 这类分隔符，去掉（markdown 表头分隔线，不是真数据）
    if ($rows.Count -ge 2) {
        $isSep = $true
        foreach ($c in $rows[1]) { if ($c -notmatch '^:?-+:?$') { $isSep = $false; break } }
        if ($isSep) { $rows.RemoveAt(1) }
    }
    if ($rows.Count -eq 0) { return "" }

    $colCount  = ($rows | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
    $pageWidth = 9026   # A4 纵向、上下左右各 1417 twips 边距后的可用宽度，和模板 sectPr 一致
    $colWidth  = [int]($pageWidth / $colCount)

    $gridXml = ""
    for ($i = 0; $i -lt $colCount; $i++) { $gridXml += "<w:gridCol w:w=`"$colWidth`"/>" }

    $borders = "<w:tblBorders>" +
        "<w:top w:val=`"single`" w:sz=`"4`" w:space=`"0`" w:color=`"BFBFBF`"/>" +
        "<w:left w:val=`"single`" w:sz=`"4`" w:space=`"0`" w:color=`"BFBFBF`"/>" +
        "<w:bottom w:val=`"single`" w:sz=`"4`" w:space=`"0`" w:color=`"BFBFBF`"/>" +
        "<w:right w:val=`"single`" w:sz=`"4`" w:space=`"0`" w:color=`"BFBFBF`"/>" +
        "<w:insideH w:val=`"single`" w:sz=`"4`" w:space=`"0`" w:color=`"BFBFBF`"/>" +
        "<w:insideV w:val=`"single`" w:sz=`"4`" w:space=`"0`" w:color=`"BFBFBF`"/>" +
        "</w:tblBorders>"

    $tblXml = "<w:tbl><w:tblPr><w:tblW w:w=`"0`" w:type=`"auto`"/>$borders<w:tblLook w:val=`"04A0`"/></w:tblPr><w:tblGrid>$gridXml</w:tblGrid>"

    for ($r = 0; $r -lt $rows.Count; $r++) {
        $isHeader = ($r -eq 0)
        $tblXml += "<w:tr>"
        for ($c = 0; $c -lt $colCount; $c++) {
            $cellText = if ($c -lt $rows[$r].Count) { $rows[$r][$c] } else { "" }
            $tcPr = "<w:tcW w:w=`"$colWidth`" w:type=`"dxa`"/><w:vAlign w:val=`"center`"/>"
            if ($isHeader) { $tcPr += "<w:shd w:val=`"clear`" w:color=`"auto`" w:fill=`"D9E2F3`"/>" }
            # 表头行强制加粗（不管原文有没有写 **加粗**）；数据行按 Split-InlineRuns 解析出来的 bold 来。
            # 数据行留空的格子不生成任何 <w:r>（保持真正的空单元格）；表头理论上不会留空，这里不用特殊处理。
            $cellRuns = ""
            if ($isHeader -or $cellText -ne "") {
                foreach ($part in (Split-InlineRuns $cellText)) {
                    $cellRuns += New-Run -text $part.text -bold ($isHeader -or $part.bold) -mono $part.mono -color $null -sz 0
                }
            }
            $pPr = "<w:pPr><w:spacing w:after=`"20`"/><w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>"
            $tblXml += "<w:tc><w:tcPr>$tcPr</w:tcPr><w:p>$pPr$cellRuns</w:p></w:tc>"
        }
        $tblXml += "</w:tr>"
    }
    $tblXml += "</w:tbl>"
    # 表格后面紧跟一个空段落，Word 里表格后必须有段落，不然文档结构会出问题
    $tblXml += "<w:p><w:pPr><w:spacing w:after=`"120`"/></w:pPr></w:p>"
    return $tblXml
}

$body  = ""
$title = "文档"

# ── 标题 + 副标题 ──────────────────────────────────────────────────────
$firstH1 = $lines | Where-Object { $_.StartsWith("# ") } | Select-Object -First 1
if ($firstH1) { $title = $firstH1.Substring(2).Trim() }

$body += "<w:p><w:pPr><w:jc w:val=`"center`"/><w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>"
$body += (New-Run -text $title -bold $true -mono $false -color "1F4E79" -sz 48)
$body += "</w:p>"
if ($Subtitle) {
    $body += "<w:p><w:pPr><w:jc w:val=`"center`"/><w:spacing w:after=`"200`"/><w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>"
    $body += (New-Run -text $Subtitle -bold $false -mono $false -color "595959" -sz 24)
    $body += "</w:p>"
}

# ── 正文解析 ────────────────────────────────────────────────────────────
$n = $lines.Count
$i = 0
while ($i -lt $n) {
    $line = $lines[$i]

    if ($line.Trim() -eq "") { $i++; continue }
    if ($line.StartsWith("# ")) { $i++; continue }              # 顶层标题已单独处理
    if ($line.Trim() -eq "---") { $i++; continue }
    if ($line.StartsWith(">")) {
        $body += New-QuoteParagraph ($line.Substring(1).Trim())
        $i++; continue
    }
    if ($line -match '^!\[(.*?)\]\((.+?)\)\s*$') {
        $body += New-ImageParagraph -srcPath $Matches[2] -caption $Matches[1]
        $i++; continue
    }

    if ($line.StartsWith("#### ")) {
        $body += New-HeadingParagraph -text $line.Substring(5).Trim() -sz 22 -before 160 -after 80 -withBorder $false
        $i++; continue
    }
    if ($line.StartsWith("### ")) {
        $body += New-HeadingParagraph -text $line.Substring(4).Trim() -sz 26 -before 240 -after 100 -withBorder $false
        $i++; continue
    }
    if ($line.StartsWith("## ")) {
        $body += New-HeadingParagraph -text $line.Substring(3).Trim() -sz 32 -before 320 -after 140 -withBorder $true
        $i++; continue
    }

    # 表格：连续的 "| ... |" 行一起收集，交给 New-Table
    if ($line.TrimStart().StartsWith("|")) {
        $tableLines = New-Object System.Collections.Generic.List[string]
        while ($i -lt $n -and $lines[$i].TrimStart().StartsWith("|")) {
            [void]$tableLines.Add($lines[$i])
            $i++
        }
        $body += New-Table $tableLines.ToArray()
        continue
    }

    # 二级缩进列表（"  - " 或 "  1. "）
    if ($line -match '^\s\s+(-|\d+\.)\s*(.*)$') {
        $marker = $Matches[1]; $text = $Matches[2]
        $prefix = if ($marker -eq '-') { "◦ " } else { "$marker " }
        $body += New-BodyParagraph -text $text -indent 700 -prefix $prefix
        $i++; continue
    }
    # 一级列表（"- " 或 "1. "）
    if ($line -match '^(-|\d+\.)\s*(.*)$') {
        $marker = $Matches[1]; $text = $Matches[2]
        $prefix = if ($marker -eq '-') { "• " } else { "$marker " }
        $body += New-BodyParagraph -text $text -indent 283 -prefix $prefix
        $i++; continue
    }

    # 普通段落
    $body += New-BodyParagraph -text $line.Trim() -indent 0 -prefix $null
    $i++
}

# ── 拼装完整 document.xml ───────────────────────────────────────────────
$preamble = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
'<w:document xmlns:wpc="http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math" xmlns:v="urn:schemas-microsoft-com:vml" xmlns:wp14="http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:w10="urn:schemas-microsoft-com:office:word" xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" xmlns:w15="http://schemas.microsoft.com/office/word/2012/wordml" xmlns:wpg="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup" xmlns:wpi="http://schemas.microsoft.com/office/word/2010/wordprocessingInk" xmlns:wne="http://schemas.microsoft.com/office/word/2006/wordml" xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" mc:Ignorable="w14 w15 wp14"><w:body>'

$sectPr = '<w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1247" w:right="1417" w:bottom="1247" w:left="1417" w:header="720" w:footer="720" w:gutter="0"/><w:cols w:space="720"/><w:docGrid w:linePitch="360"/></w:sectPr>'

$fullXml = $preamble + $body + $sectPr + '</w:body></w:document>'

# 校验 XML 合法，不合法就不往下走，避免生成一个打不开的坏 docx
$xmlDoc = New-Object System.Xml.XmlDocument
$xmlDoc.LoadXml($fullXml)
$paraCount = $xmlDoc.SelectNodes('//*[local-name()=''p'']').Count
$tblCount  = $xmlDoc.SelectNodes('//*[local-name()=''tbl'']').Count

# ── 打包成 docx ──────────────────────────────────────────────────────────
Add-Type -AssemblyName System.IO.Compression.FileSystem

$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("docxbuild_" + [Guid]::NewGuid().ToString("N"))
Copy-Item -Path $templateDir -Destination $tmpDir -Recurse
# UTF8Encoding($false) 里的 false = 写文件时不加 BOM 头：Word 打开 docx 内部的 xml 不需要 BOM，
# 加了反而可能被极少数解析器误当成文件内容的一部分，所以这里统一不写。
[System.IO.File]::WriteAllText((Join-Path $tmpDir "word\document.xml"), $fullXml, (New-Object System.Text.UTF8Encoding($false)))

# 正文里引用了图片的话，把图片文件拷进 word/media/，并把"rId 对应哪个文件"写进 document.xml.rels，
# 还要在 [Content_Types].xml 里声明 png/jpg 这些扩展名是什么内容类型——三处东西不全，Word 会打不开或图片显示成红叉。
if ($imageRels.Count -gt 0) {
    $mediaDir = Join-Path $tmpDir "word\media"
    New-Item -ItemType Directory -Path $mediaDir -Force | Out-Null
    foreach ($rel in $imageRels) {
        Copy-Item -Path $rel.SourcePath -Destination (Join-Path $mediaDir $rel.FileName) -Force
    }

    $relsPath = Join-Path $tmpDir "word\_rels\document.xml.rels"
    $relsXml  = [System.IO.File]::ReadAllText($relsPath, [System.Text.Encoding]::UTF8)
    $newRels  = ($imageRels | ForEach-Object {
        "<Relationship Id=`"$($_.RId)`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image`" Target=`"media/$($_.FileName)`"/>"
    }) -join ""
    $relsXml  = $relsXml -replace '</Relationships>', "$newRels</Relationships>"
    [System.IO.File]::WriteAllText($relsPath, $relsXml, (New-Object System.Text.UTF8Encoding($false)))

    $ctPath  = Join-Path $tmpDir "[Content_Types].xml"
    $ctXml   = [System.IO.File]::ReadAllText($ctPath, [System.Text.Encoding]::UTF8)
    $usedExts = $imageRels.Ext | Select-Object -Unique
    $extToType = @{ png = "image/png"; jpg = "image/jpeg"; jpeg = "image/jpeg"; gif = "image/gif" }
    $newDefaults = ($usedExts | Where-Object { $ctXml -notmatch "Extension=`"$_`"" } | ForEach-Object {
        "<Default Extension=`"$_`" ContentType=`"$($extToType[$_])`"/>"
    }) -join ""
    $ctXml = $ctXml -replace '</Types>', "$newDefaults</Types>"
    [System.IO.File]::WriteAllText($ctPath, $ctXml, (New-Object System.Text.UTF8Encoding($false)))
}

$tmpDocx = Join-Path ([System.IO.Path]::GetTempPath()) ("gen_" + [Guid]::NewGuid().ToString("N") + ".docx")
[System.IO.Compression.ZipFile]::CreateFromDirectory($tmpDir, $tmpDocx, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Copy-Item -Path $tmpDocx -Destination $outDocx -Force
Remove-Item -Path $tmpDir -Recurse -Force
Remove-Item -Path $tmpDocx -Force

Write-Output "已生成：$outDocx （$paraCount 段，$tblCount 张表）"
