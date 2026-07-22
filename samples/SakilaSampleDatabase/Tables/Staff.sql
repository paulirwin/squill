-- The Sakila `staff` table. The other half of the circular store<->staff foreign-key pair
-- (see Store.sql). Demonstrates a `mediumblob` binary column (`picture`) and a `boolean`
-- column with a DEFAULT — in MySQL/MariaDB `boolean` is an alias for `tinyint(1)`.
CREATE TABLE staff
(
    staff_id    int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    address_id  int unsigned NOT NULL,
    picture     mediumblob,
    email       varchar(50),
    store_id    int unsigned NOT NULL,
    active      boolean NOT NULL DEFAULT true,
    username    varchar(16) NOT NULL,
    password    varchar(40),
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_staff_store FOREIGN KEY (store_id) REFERENCES store (store_id),
    CONSTRAINT fk_staff_address FOREIGN KEY (address_id) REFERENCES address (address_id)
);

CREATE INDEX idx_fk_store_id ON staff (store_id);
CREATE INDEX idx_fk_address_id ON staff (address_id);
