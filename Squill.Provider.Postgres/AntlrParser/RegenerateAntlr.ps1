param 
(
    $pathToAntlrJar
)

java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.Provider.Postgres.AntlrParser PostgresLexer.g4
java -jar "$pathToAntlrJar" -Dlanguage=CSharp -package Squill.Provider.Postgres.AntlrParser -visitor -no-listener PostgresParser.g4
