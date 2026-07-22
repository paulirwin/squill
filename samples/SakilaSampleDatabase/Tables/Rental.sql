-- The Sakila `rental` table: one row per rental transaction. Demonstrates a composite UNIQUE
-- index across three columns (a business rule: the same copy cannot be rented to the same
-- customer at the same instant), a nullable `return_date`, and three named foreign keys with
-- their backing indexes.
--
-- Note the surrogate key is a plain signed `int` here (not `int unsigned`), matching the
-- canonical schema.
CREATE TABLE rental
(
    rental_id    int NOT NULL AUTO_INCREMENT PRIMARY KEY,
    rental_date  datetime NOT NULL,
    inventory_id int unsigned NOT NULL,
    customer_id  int unsigned NOT NULL,
    return_date  datetime,
    staff_id     int unsigned NOT NULL,
    last_update  timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT uq_rental_date_inventory_customer UNIQUE (rental_date, inventory_id, customer_id),
    CONSTRAINT fk_rental_staff FOREIGN KEY (staff_id) REFERENCES staff (staff_id),
    CONSTRAINT fk_rental_inventory FOREIGN KEY (inventory_id) REFERENCES inventory (inventory_id),
    CONSTRAINT fk_rental_customer FOREIGN KEY (customer_id) REFERENCES customer (customer_id)
);

CREATE INDEX idx_fk_inventory_id ON rental (inventory_id);
CREATE INDEX idx_fk_customer_id ON rental (customer_id);
CREATE INDEX idx_fk_staff_id ON rental (staff_id);
