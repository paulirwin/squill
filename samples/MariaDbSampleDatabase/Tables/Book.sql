CREATE TABLE book
(
    book_id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
    author_id int NOT NULL,
    title     varchar(400) NOT NULL,
    CONSTRAINT fk_book_author FOREIGN KEY (author_id) REFERENCES author (author_id)
);

CREATE INDEX ix_book_author_id ON book (author_id);
