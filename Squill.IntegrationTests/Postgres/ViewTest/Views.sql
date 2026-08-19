-- Views (issue #42) exercised end-to-end against a real PostgreSQL database.
--
-- A view is deployed after the tables it selects from, so a body may reference any table
-- declared in the project.

CREATE TABLE author (
    author_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name varchar(200) NOT NULL,
    active boolean NOT NULL
);

CREATE TABLE book (
    book_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    author_id integer NOT NULL REFERENCES author (author_id),
    title varchar(400) NOT NULL,
    copies integer NOT NULL
);

-- Plain column references: each names the view column after the selected column.
CREATE VIEW active_author AS
SELECT author_id, name
FROM author
WHERE active;

-- An explicit column list names the columns outright, independently of the select list.
CREATE VIEW author_label (id, label) AS
SELECT author_id, name
FROM author;

-- An aliased expression takes its alias. Without one there would be no name to model the
-- column under, which Squill reports as a build error rather than inventing "?column?".
CREATE VIEW book_stock AS
SELECT book_id, title, copies * 2 AS double_copies
FROM book;

-- SELECT * is expanded against the table's declared columns, so the view's modeled shape
-- matches what PostgreSQL reports back from the catalog.
CREATE VIEW all_books AS
SELECT * FROM book;

-- Issue #208: the clauses that decide how a view executes. Measured on PostgreSQL 18, each
-- lands in pg_class.reloptions and is reported back exactly, so unlike the query they do
-- round-trip and are modeled.

-- WITH CHECK OPTION constrains what may be written through the view. A bare one is stored
-- as cascaded, which is why the model records CASCADED rather than a third "unspecified".
CREATE VIEW active_author_checked AS
SELECT author_id, name, active
FROM author
WHERE active
WITH CASCADED CHECK OPTION;

-- LOCAL is the other half of the same facet.
CREATE VIEW active_author_local AS
SELECT author_id, name, active
FROM author
WHERE active
WITH LOCAL CHECK OPTION;

-- security_invoker decides whose privileges the body runs under, so dropping it moved a
-- privilege boundary rather than losing a cosmetic facet.
CREATE VIEW author_invoker WITH (security_invoker = true) AS
SELECT author_id, name
FROM author;

-- An explicitly written default. PostgreSQL records security_invoker=false rather than
-- dropping it, so this must survive as a distinct state from declaring nothing.
CREATE VIEW author_invoker_false WITH (security_invoker = false) AS
SELECT author_id, name
FROM author;

CREATE VIEW author_barrier WITH (security_barrier = true) AS
SELECT author_id, name
FROM author;
