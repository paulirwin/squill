-- Each schema object lives in its own .sql file as a declarative CREATE statement.
-- Squill builds a DACPAC from these files, then deploys by diffing the model against
-- the target. Uncomment the example below (or replace it with your own tables) to get
-- started, and add more .sql files as your schema grows.

-- CREATE TABLE example
-- (
--     example_id integer NOT NULL PRIMARY KEY,
--     name varchar(200) NOT NULL
-- );
