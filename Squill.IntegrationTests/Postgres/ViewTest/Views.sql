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
