-- A second table in the "audit" schema, with a foreign key to another table in the same
-- schema (audit.book_change) — showing a schema that contains several related tables.
CREATE TABLE audit.change_review
(
    change_review_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    book_change_id   integer NOT NULL REFERENCES audit.book_change (book_change_id),
    reviewer         varchar(200) NOT NULL
);
