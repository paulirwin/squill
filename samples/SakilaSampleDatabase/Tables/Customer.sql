-- The Sakila `customer` table. Demonstrates a `datetime` column with no auto-update
-- (`create_date`, set once at creation) alongside a `timestamp ... ON UPDATE CURRENT_TIMESTAMP`
-- audit column — a useful contrast between the two temporal types. Note that unlike the other
-- tables here, `last_update` is nullable (no NOT NULL), matching the canonical schema.
CREATE TABLE customer
(
    customer_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    store_id    int unsigned NOT NULL,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    email       varchar(50),
    address_id  int unsigned NOT NULL,
    active      boolean NOT NULL DEFAULT true,
    create_date datetime NOT NULL,
    last_update timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_customer_address FOREIGN KEY (address_id) REFERENCES address (address_id),
    CONSTRAINT fk_customer_store FOREIGN KEY (store_id) REFERENCES store (store_id)
);

CREATE INDEX idx_fk_store_id ON customer (store_id);
CREATE INDEX idx_fk_address_id ON customer (address_id);
CREATE INDEX idx_last_name ON customer (last_name);
