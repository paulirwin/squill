-- A view (issue #42). Views are deployed after the tables they select from, so a view may
-- reference any table declared in the project.
--
-- Note that a view's query is carried for scripting but does not take part in the model's
-- identity: PostgreSQL rewrites a view's query when it stores it, so the declared text
-- could never be compared against what the database reports back. A view is compared on its
-- name and column list instead, which means changing only the WHERE clause below (without
-- changing the columns) would not be picked up as a change to redeploy.
CREATE VIEW book_detail AS
SELECT b.book_id, b.title, a.name AS author_name
FROM book b, author a
WHERE b.author_id = a.author_id;

-- An explicit column list names the view's columns outright, independently of what the
-- select list happens to call them.
CREATE VIEW author_summary (id, display_name) AS
SELECT author_id, name
FROM author;
