param 
(
    $pathToAntlrJar
)

java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.PostgresParser PostgreSQLLexer.g4
java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.PostgresParser -visitor -no-listener PostgreSQLParser.g4
