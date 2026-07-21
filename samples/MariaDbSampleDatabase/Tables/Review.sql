-- A second FK back to book, plus a few MariaDB column features: a defaulted status,
-- a decimal rating, and a unique constraint. MariaDB has no separate schema namespace
-- (the database is the schema), so every table lives together — unlike the PostgreSQL
-- sample, which places its audit tables in a declared "audit" schema.
CREATE TABLE review
(
    review_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
    book_id   int NOT NULL,
    reviewer  varchar(200) NOT NULL,
    rating    decimal(3, 2) NOT NULL DEFAULT 0,
    status    varchar(20) NOT NULL DEFAULT 'pending',
    CONSTRAINT fk_review_book FOREIGN KEY (book_id) REFERENCES book (book_id),
    CONSTRAINT uq_review_book_reviewer UNIQUE (book_id, reviewer)
);
