-- A procedure in the non-public "audit" schema, alongside the tables it writes to. Like a
-- table, a procedure in a non-public schema needs that schema declared in the project.
CREATE PROCEDURE audit.record_book_change(book_id integer, change_note varchar(500))
LANGUAGE sql
AS $$
INSERT INTO audit.book_change (book_id, change_note) VALUES (book_id, change_note);
$$;
