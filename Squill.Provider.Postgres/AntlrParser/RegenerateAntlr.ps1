param 
(
    $pathToAntlrJar
)

java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.Provider.Postgres.AntlrParser PostgreSQLLexer.g4
java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.Provider.Postgres.AntlrParser -visitor -no-listener PostgreSQLParser.g4
