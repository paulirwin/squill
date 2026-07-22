-- The Sakila `payment` table: one row per payment. Demonstrates a foreign key with a
-- referential action — `fk_payment_rental` is declared `ON DELETE SET NULL`, so deleting a
-- rental nulls out the payment's `rental_id` rather than blocking the delete (which is why
-- `rental_id` is nullable). `payment_id` uses `int unsigned` for its surrogate key.
CREATE TABLE payment
(
    payment_id   int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    customer_id  int unsigned NOT NULL,
    staff_id     int unsigned NOT NULL,
    rental_id    int,
    amount       decimal(5, 2) NOT NULL,
    payment_date datetime NOT NULL,
    last_update  timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_payment_rental FOREIGN KEY (rental_id) REFERENCES rental (rental_id) ON DELETE SET NULL,
    CONSTRAINT fk_payment_customer FOREIGN KEY (customer_id) REFERENCES customer (customer_id),
    CONSTRAINT fk_payment_staff FOREIGN KEY (staff_id) REFERENCES staff (staff_id)
);

CREATE INDEX idx_fk_staff_id ON payment (staff_id);
CREATE INDEX idx_fk_customer_id ON payment (customer_id);
