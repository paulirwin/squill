-- A view (issue #42). Views are deployed after the tables they select from, so a view may
-- reference any table declared in the project.
--
-- Note that a view's query is carried for scripting but does not take part in the model's
-- identity: MariaDB and MySQL each rewrite a view's query when they store it — and not even
-- the same way as each other — so the declared text could never be compared against what
-- the database reports back. A view is compared on its name and column list instead, which
-- means changing only the WHERE clause below (without changing the columns) would not be
-- picked up as a change to redeploy.
CREATE VIEW book_detail AS
SELECT book.book_id, book.title, author.name AS author_name
FROM book, author
WHERE book.author_id = author.author_id;

-- An explicit column list names the view's columns outright, independently of what the
-- select list happens to call them.
CREATE VIEW review_summary (id, book, reviewer_name, score) AS
SELECT review_id, book_id, reviewer, rating
FROM review;
