<#
.SYNOPSIS
    Regenerates the ANTLR lexer/parser, optionally re-vendoring the grammar from upstream.

.DESCRIPTION
    The .g4 grammars and the two *Base.cs files are vendored verbatim from
    antlr/grammars-v4 (sql/postgresql). Per CLAUDE.md we never hand-edit them: a grammar
    problem goes upstream as an issue/PR, and we re-vendor once it lands.

    The upstream *Base.cs files carry no namespace and are not nullable-annotated, so they
    need two mechanical edits to compile here. Those edits are applied by this script rather
    than by hand, so re-vendoring stays reproducible and nobody has to remember them.

.PARAMETER PathToAntlrJar
    Path to antlr-4.13.1-complete.jar. Match the Antlr4.Runtime.Standard version in the
    .csproj; a mismatch produces a parser the runtime may reject.

.PARAMETER Revendor
    Re-download the grammar and base files from upstream master before generating.

.EXAMPLE
    ./RegenerateAntlr.ps1 -PathToAntlrJar ~/antlr-4.13.1-complete.jar

.EXAMPLE
    ./RegenerateAntlr.ps1 -PathToAntlrJar ~/antlr-4.13.1-complete.jar -Revendor
#>
param
(
    [Parameter(Mandatory = $true)]
    [string] $PathToAntlrJar,

    [switch] $Revendor
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$upstream = 'https://raw.githubusercontent.com/antlr/grammars-v4/master/sql/postgresql'

# The local delta applied to each vendored base file. Upstream ships these without a
# namespace; PostgreSQLParserBase.cs additionally is not nullable-annotated and would trip
# CS8600/CS8602, which TreatWarningsAsErrors turns into build errors.
$baseFiles = @(
    @{ Name = 'PostgreSQLLexerBase.cs';  Url = "$upstream/CSharp/PostgreSQLLexerBase.cs";  DisableNullable = $false }
    @{ Name = 'PostgreSQLParserBase.cs'; Url = "$upstream/CSharp/PostgreSQLParserBase.cs"; DisableNullable = $true  }
)

if ($Revendor)
{
    Write-Host 'Re-vendoring grammar from upstream master...'

    foreach ($g4 in 'PostgreSQLLexer.g4', 'PostgreSQLParser.g4')
    {
        Write-Host "  $g4"
        Invoke-WebRequest -Uri "$upstream/$g4" -OutFile $g4
    }

    foreach ($file in $baseFiles)
    {
        Write-Host "  $($file.Name)"
        Invoke-WebRequest -Uri $file.Url -OutFile $file.Name
    }
}

# Applied whether or not we just re-vendored, so a base file that was replaced by hand still
# ends up correct. Both edits are idempotent.
foreach ($file in $baseFiles)
{
    $content = Get-Content -Raw $file.Name

    if ($content -notmatch '(?m)^namespace Squill\.PostgresParser;')
    {
        $preamble = @()

        if ($file.DisableNullable)
        {
            $preamble += '// <squill> Vendored from antlr/grammars-v4 (sql/postgresql/CSharp) alongside the .g4'
            $preamble += '// files; re-copied verbatim by RegenerateAntlr.ps1 -Revendor, so it is not hand-edited.'
            $preamble += '// It is not nullable-annotated and trips CS8600/CS8602, which TreatWarningsAsErrors'
            $preamble += '// would turn into build errors, so nullable analysis is disabled for this file. </squill>'
            $preamble += '#nullable disable'
            $preamble += ''
        }
        else
        {
            $preamble += '// <squill> Vendored from antlr/grammars-v4 (sql/postgresql/CSharp); re-copied verbatim'
            $preamble += '// by RegenerateAntlr.ps1 -Revendor, so it is not hand-edited. </squill>'
        }

        $preamble += 'namespace Squill.PostgresParser;'
        $preamble += ''

        # Insert after the last top-of-file using directive, which is where the namespace
        # has to go in a file-scoped-namespace layout.
        $usings = [regex]::Matches($content, '(?m)^using [^\r\n]*;[ \t]*\r?$')

        if ($usings.Count -eq 0)
        {
            throw "$($file.Name): no using directives found; cannot place the namespace. Upstream layout changed — patch this script."
        }

        $at = $usings[$usings.Count - 1].Index + $usings[$usings.Count - 1].Length
        $content = $content.Substring(0, $at) + "`n`n" + ($preamble -join "`n") + $content.Substring($at)

        Set-Content -Path $file.Name -Value $content -NoNewline
        Write-Host "Patched $($file.Name) (namespace$(if ($file.DisableNullable) { ' + #nullable disable' }))"
    }
}

java -jar "$PathToAntlrJar" -Dlanguage=CSharp -package Squill.PostgresParser PostgreSQLLexer.g4
java -jar "$PathToAntlrJar" -Dlanguage=CSharp -package Squill.PostgresParser -visitor -no-listener PostgreSQLParser.g4

Write-Host 'Done. Build and run the full test suite — the tests assert parse-tree shapes and'
Write-Host 'rendered SQL, so they are the check that a re-vendor did not change behaviour.'
