param
(
    $pathToAntlrJar
)

java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.MariaDbParser MariaDBLexer.g4
java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.MariaDbParser -visitor -no-listener MariaDBParser.g4
