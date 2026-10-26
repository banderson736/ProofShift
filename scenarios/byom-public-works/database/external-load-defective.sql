TRUNCATE TABLE public.work_order_history, public.work_orders, public.assets;

DO $$
DECLARE
    i integer;
BEGIN
    FOR i IN 1..12 LOOP
        IF i <> 10 THEN
            INSERT INTO public.assets(asset_id, asset_name, asset_type, active)
            VALUES
            (
                'A' || lpad(i::text, 3, '0'),
                'Synthetic Asset ' || lpad(i::text, 3, '0'),
                CASE WHEN i % 2 = 0 THEN 'VEHICLE' ELSE 'FACILITY' END,
                i % 5 <> 0
            );
        END IF;
    END LOOP;

    FOR i IN 1..30 LOOP
        INSERT INTO public.work_orders(work_order_id, asset_id, status, created_at, description)
        VALUES
        (
            'W' || lpad(i::text, 4, '0'),
            'A' || lpad((((i - 1) % 12) + 1)::text, 3, '0'),
            CASE
                WHEN i = 7 THEN 'NEW'
                WHEN i % 3 = 0 THEN 'CLOSED'
                WHEN i % 3 = 1 THEN 'OPEN'
                ELSE 'NEW'
            END,
            DATE '2025-01-01' + i,
            'Synthetic work order ' || lpad(i::text, 4, '0')
        );
    END LOOP;

    FOR i IN 1..60 LOOP
        IF i <> 8 THEN
            INSERT INTO public.work_order_history(history_id, work_order_id, status, changed_at, note)
            VALUES
            (
                'H' || lpad(i::text, 4, '0'),
                'W' || lpad((((i - 1) / 2) + 1)::text, 4, '0'),
                CASE WHEN i % 2 = 0 THEN 'CLOSED' ELSE 'OPEN' END,
                DATE '2025-01-01' + i,
                'Synthetic history event ' || lpad(i::text, 4, '0')
            );
        END IF;
    END LOOP;

    UPDATE public.work_orders SET asset_id = 'A001' WHERE work_order_id = 'W0012';
END;
$$;
