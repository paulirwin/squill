-- A table in the non-public "audit" schema. Its name is schema-qualified, and it holds a
-- cross-schema foreign key back to public.book — Squill qualifies the reference so it
-- resolves regardless of the session search_path.
CREATE TABLE audit.book_change
(
    book_change_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    book_id        integer NOT NULL REFERENCES public.book (book_id),
    change_note    varchar(500) NOT NULL
);

CREATE INDEX ix_book_change_book_id ON audit.book_change (book_id);
