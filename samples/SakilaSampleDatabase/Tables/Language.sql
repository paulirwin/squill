-- The Sakila `language` lookup table. Uses a fixed-width `char(20)` for the name (rather than
-- `varchar`), matching the canonical schema. Referenced twice by `film` (spoken language and
-- original language).
CREATE TABLE language
(
    language_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name        char(20) NOT NULL,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
