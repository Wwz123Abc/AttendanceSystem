# 把 docs/更新日志.md 的最新内容，同步生成成 docs/考勤系统更新日志_完整版.docx。
# 用法：每次给 docs/更新日志.md 加完一条新记录后，跑一下这个脚本，Word 版本就自动跟着更新了。
#   powershell -ExecutionPolicy Bypass -File tools/update_changelog_docx.ps1
#
# 原理：docx 文件本质是一个 zip 包，里面的 word/document.xml 存正文内容，其它文件（样式/字体/主题）
# 不需要每次都变。所以这里用 tools/docx_template 当"空白模板"，每次只重新生成 document.xml，
# 再连同模板里其它不变的文件一起打包成最终的 docx——不依赖 Word/Office，也不需要装 Node/Python。
$ErrorActionPreference = "Stop"

$root       = Split-Path -Parent $PSScriptRoot   # tools/ 的上一级，即 AttendanceSystem 项目根目录
$mdPath     = Join-Path $root "docs\更新日志.md"
$templateDir = Join-Path $PSScriptRoot "docx_template"
$outDocx    = Join-Path $root "docs\考勤系统更新日志_完整版.docx"

if (-not (Test-Path $mdPath)) { throw "找不到 $mdPath" }
if (-not (Test-Path $templateDir)) { throw "找不到模板目录 $templateDir" }

$lines = [System.IO.File]::ReadAllLines($mdPath, [System.Text.Encoding]::UTF8)

function Escape-Xml([string]$s) {
    $s = $s -replace '&', '&amp;'
    $s = $s -replace '<', '&lt;'
    $s = $s -replace '>', '&gt;'
    return $s
}

function New-Run([string]$text, [bool]$bold, [string]$color, [int]$sz) {
    $rPr = "<w:rPr>"
    if ($bold) { $rPr += "<w:b/>" }
    if ($color) { $rPr += "<w:color w:val=`"$color`"/>" }
    if ($sz -gt 0) { $rPr += "<w:sz w:val=`"$sz`"/>" }
    $rPr += "<w:lang w:eastAsia=`"zh-CN`"/></w:rPr>"
    $t = Escape-Xml $text
    $preserve = ""
    if ($t -match '^\s' -or $t -match '\s$') { $preserve = ' xml:space="preserve"' }
    return "<w:r>$rPr<w:t$preserve>$t</w:t></w:r>"
}

# 把一段文本按 **粗体** 拆成 (text, isBold) 片段列表
function Split-BoldRuns([string]$text) {
    $parts = New-Object System.Collections.ArrayList
    $regex = [regex]'\*\*(.+?)\*\*'
    $lastEnd = 0
    foreach ($m in $regex.Matches($text)) {
        if ($m.Index -gt $lastEnd) {
            [void]$parts.Add(@{ text = $text.Substring($lastEnd, $m.Index - $lastEnd); bold = $false })
        }
        [void]$parts.Add(@{ text = $m.Groups[1].Value; bold = $true })
        $lastEnd = $m.Index + $m.Length
    }
    if ($lastEnd -lt $text.Length) {
        [void]$parts.Add(@{ text = $text.Substring($lastEnd); bold = $false })
    }
    if ($parts.Count -eq 0) { [void]$parts.Add(@{ text = $text; bold = $false }) }
    return $parts
}

function New-BodyParagraph([string]$text, [int]$indent, [string]$prefix) {
    $runsXml = ""
    if ($prefix) { $runsXml += New-Run -text $prefix -bold $false -color $null -sz 0 }
    foreach ($part in (Split-BoldRuns $text)) {
        $runsXml += New-Run -text $part.text -bold $part.bold -color $null -sz 0
    }
    $pPr = "<w:ind w:left=`"$indent`"/>"
    return "<w:p><w:pPr>$pPr<w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>$runsXml</w:p>"
}

function New-HeadingParagraph([string]$text, [int]$sz, [int]$before, [int]$after, [bool]$withBorder) {
    $pPr = "<w:spacing w:before=`"$before`" w:after=`"$after`"/>"
    if ($withBorder) { $pPr += "<w:pBdr><w:bottom w:val=`"single`" w:sz=`"6`" w:space=`"4`" w:color=`"1F4E79`"/></w:pBdr>" }
    $run = New-Run -text $text -bold $true -color "1F4E79" -sz $sz
    return "<w:p><w:pPr>$pPr<w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>$run</w:p>"
}

$body = ""

# ── 标题 + 生成日期（用运行脚本当天的日期，不用每次手改）+ 说明 ──────────────
$today = Get-Date -Format "yyyy年M月d日"
$body += "<w:p><w:pPr><w:jc w:val=`"center`"/><w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>"
$body += (New-Run -text "考勤系统更新日志" -bold $true -color "1F4E79" -sz 48)
$body += "</w:p>"
$body += "<w:p><w:pPr><w:jc w:val=`"center`"/><w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>"
$body += (New-Run -text "生成日期：$today" -bold $false -color "595959" -sz 24)
$body += "</w:p>"
$body += "<w:p><w:pPr><w:spacing w:after=`"200`"/><w:rPr><w:lang w:eastAsia=`"zh-CN`"/></w:rPr></w:pPr>"
$body += (New-Run -text "说明：这份文档记录考勤系统每次功能改动，语言尽量简单，方便非技术人员看懂；每次改动都会在最上面加一条新记录（时间倒序）。" -bold $false -color "595959" -sz 20)
$body += "</w:p>"

# ── 解析正文 ────────────────────────────────────────────────────────────
$n = $lines.Count
for ($i = 0; $i -lt $n; $i++) {
    $line = $lines[$i]
    if ($line.Trim() -eq "") { continue }
    if ($line.StartsWith("# ")) { continue }          # 顶层标题，已单独处理
    if ($line.StartsWith(">")) { continue }           # 说明性引用块，已单独处理
    if ($line.Trim() -eq "---") { continue }

    if ($line.StartsWith("## ")) {
        $text = $line.Substring(3).Trim()
        $body += New-HeadingParagraph -text $text -sz 32 -before 400 -after 160 -withBorder $true
        continue
    }
    if ($line.StartsWith("### ")) {
        # 跳过"空标题"：如果紧接着的下一个非空行还是标题，说明这是历史遗留的重复/悬空标题，没有实际内容
        $next = $null
        for ($j = $i + 1; $j -lt $n; $j++) {
            if ($lines[$j].Trim() -ne "") { $next = $lines[$j]; break }
        }
        if ($next -and $next.StartsWith("### ")) { continue }

        $text = $line.Substring(4).Trim()
        $body += New-HeadingParagraph -text $text -sz 26 -before 240 -after 100 -withBorder $false
        continue
    }

    # 二级缩进的列表项（"  - " 或 "  1. " 这种，前面至少 2 个空格）
    if ($line -match '^\s\s+(-|\d+\.)\s*(.*)$') {
        $marker = $Matches[1]
        $text   = $Matches[2]
        $prefix = if ($marker -eq '-') { "◦ " } else { "$marker " }
        $body += New-BodyParagraph -text $text -indent 700 -prefix $prefix
        continue
    }
    # 一级列表项（"- " 开头，不带前导空格）
    if ($line -match '^-\s*(.*)$') {
        $text = $Matches[1]
        $body += New-BodyParagraph -text $text -indent 283 -prefix "• "
        continue
    }

    # 其余：普通正文段落（不带项目符号）
    $body += New-BodyParagraph -text $line.Trim() -indent 283 -prefix $null
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

# ── 打包成 docx：复制模板到临时目录、替换 document.xml、压缩、原子替换目标文件 ──
Add-Type -AssemblyName System.IO.Compression.FileSystem

$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) ("docxbuild_" + [Guid]::NewGuid().ToString("N"))
Copy-Item -Path $templateDir -Destination $tmpDir -Recurse
[System.IO.File]::WriteAllText((Join-Path $tmpDir "word\document.xml"), $fullXml, (New-Object System.Text.UTF8Encoding($false)))

$tmpDocx = Join-Path ([System.IO.Path]::GetTempPath()) ("changelog_" + [Guid]::NewGuid().ToString("N") + ".docx")
[System.IO.Compression.ZipFile]::CreateFromDirectory($tmpDir, $tmpDocx, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Copy-Item -Path $tmpDocx -Destination $outDocx -Force
Remove-Item -Path $tmpDir -Recurse -Force
Remove-Item -Path $tmpDocx -Force

Write-Output "已更新：$outDocx （共 $paraCount 段）"
