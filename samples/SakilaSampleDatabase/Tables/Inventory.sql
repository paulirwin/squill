-- The Sakila `inventory` table: one row per physical copy of a film held at a store.
-- Demonstrates a multi-column index (`idx_store_id_film_id`) in addition to the single-column
-- FK-backing index, and named foreign keys to both `store` and `film`.
CREATE TABLE inventory
(
    inventory_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    film_id      int unsigned NOT NULL,
    store_id     int unsigned NOT NULL,
    last_update  timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_inventory_store FOREIGN KEY (store_id) REFERENCES store (store_id),
    CONSTRAINT fk_inventory_film FOREIGN KEY (film_id) REFERENCES film (film_id)
);

CREATE INDEX idx_fk_film_id ON inventory (film_id);
CREATE INDEX idx_store_id_film_id ON inventory (store_id, film_id);
