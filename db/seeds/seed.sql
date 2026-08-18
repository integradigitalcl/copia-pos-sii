-- Minimal seed data for local development (DETERMINISTIC IDs).
-- This lets you run two terminals without querying the DB.

-- IDs (copy/paste into terminal args)
-- tenantId     = 11111111-1111-1111-1111-111111111111
-- branchId     = 22222222-2222-2222-2222-222222222222
-- terminalAId  = aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa
-- terminalBId  = bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb
-- userAdminId  = 99999999-9999-9999-9999-999999999999
-- cashSessionA = 55555555-5555-5555-5555-555555555555
-- cashSessionB = 66666666-6666-6666-6666-666666666666
-- product1Id   = 33333333-3333-3333-3333-333333333333  (SKU-COCA-600)
-- product2Id   = 44444444-4444-4444-4444-444444444444  (SKU-PAN-001)

INSERT INTO tenants(tenant_id, name)
VALUES ('11111111-1111-1111-1111-111111111111', 'Demo Tenant')
ON CONFLICT (tenant_id) DO NOTHING;

INSERT INTO branches(branch_id, tenant_id, name, timezone)
VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'Sucursal Centro', 'America/Mexico_City')
ON CONFLICT (branch_id) DO NOTHING;

INSERT INTO terminals(terminal_id, tenant_id, branch_id, name, machine_name, status)
VALUES
  ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'Caja A', 'POS-A', 'active'),
  ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'Caja B', 'POS-B', 'active')
ON CONFLICT (terminal_id) DO NOTHING;

INSERT INTO users(user_id, tenant_id, username, password_hash, display_name, is_active)
VALUES ('99999999-9999-9999-9999-999999999999', '11111111-1111-1111-1111-111111111111', 'admin', 'dev-only', 'Admin', true)
ON CONFLICT (user_id) DO NOTHING;

INSERT INTO products(product_id, tenant_id, sku, barcode, name, unit, tax_rate, price, cost, is_active)
VALUES
  ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'SKU-COCA-600', '750000000001', 'Refresco 600ml', 'ea', 0.16, 18.50, 10.00, true),
  ('44444444-4444-4444-4444-444444444444', '11111111-1111-1111-1111-111111111111', 'SKU-PAN-001',  '750000000002', 'Pan dulce',      'ea', 0.00, 12.00,  6.00, true)
ON CONFLICT (product_id) DO NOTHING;

INSERT INTO inventory(tenant_id, branch_id, product_id, on_hand, reserved)
VALUES
  ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', '33333333-3333-3333-3333-333333333333', 10, 0),
  ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', '44444444-4444-4444-4444-444444444444',  5, 0)
ON CONFLICT (tenant_id, branch_id, product_id) DO NOTHING;

INSERT INTO counters(tenant_id, branch_id, key, value)
VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'ticket', 1000)
ON CONFLICT (tenant_id, branch_id, key) DO NOTHING;

INSERT INTO cash_sessions(cash_session_id, tenant_id, branch_id, terminal_id, opened_by, opening_amount, status)
VALUES
  ('55555555-5555-5555-5555-555555555555', '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '99999999-9999-9999-9999-999999999999', 0, 'open'),
  ('66666666-6666-6666-6666-666666666666', '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '99999999-9999-9999-9999-999999999999', 0, 'open')
ON CONFLICT (cash_session_id) DO NOTHING;

