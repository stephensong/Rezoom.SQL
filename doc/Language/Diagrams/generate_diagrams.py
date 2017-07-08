from railroad_diagrams import *

def language(document, tag):
    return 'https://rspeele.gitbooks.io/rezoom-sql/doc/Language/' + document + '.html#' + tag

def expr():
    return NonTerminal('expr', language('Expr', 'expr'))

def select_core():
    return NonTerminal('select-core', language('SelectStmt', 'select-core'))

def select_stmt():
    return NonTerminal('select-stmt', language('SelectStmt', 'select-stmt'))

def name():
    return NonTerminal('name', language('Name', 'name'))

def bind_parameter():
    return NonTerminal('@bind-parameter')

def type_name():
    return NonTerminal('type-name', language('DataTypes', 'type-name'))

def object_name():
    return NonTerminal('object-name', language('Name', 'object-name'))

def column_name():
    return NonTerminal('column-name', language('Name', 'column-name'))

def select_property():
    return NonTerminal('select-property', language('SelectStmt', 'select-property'))

def order_direction():
    return Optional(Choice(0, 'ASC', 'DESC'), 'skip')

def table_expr():
    return NonTerminal('table-expr', language('SelectStmt', 'table-expr'))

def compound_expr():
    return NonTerminal('compound-expr', language('SelectStmt', 'compound-expr'))

def column_constraint():
    return NonTerminal('compound-expr', language('CreateTableStmt', 'column-constraint'))

def column_def():
    return NonTerminal('column-def', language('CreateTableStmt', 'column-def'))

def literal():
    return NonTerminal('literal', language('Literal', 'literal'))

def primary_key_clause():
    return Sequence(
        'PRIMARY',
        'KEY',
        order_direction(),
        Optional('AUTOINCREMENT', 'skip'))

def foreign_key_clause():
    return Sequence(
        'REFERENCES',
        object_name(),
        '(',
        ZeroOrMore(name(), ','),
        ')')

def export(filename, diagram):
    with open(filename, 'w') as f:
        diagram.writeSvg(f.write)

export('RailroadDemo.svg', Diagram(
    Stack(
        Sequence(
            'REQUIRED KEYWORD',
            Optional(Sequence('OPTIONAL KEYWORD')),
            OneOrMore('MANY OF THESE')),
        Sequence(
            '(',
            ZeroOrMore(NonTerminal('other-thing'), ','),
            ')'))))

export('WhitespaceOrComment.svg', Diagram(
    Choice(0,
        Choice(0,
            ' ',
            '\\r',
            '\\n',
            '\\t'),
        Sequence('--', ZeroOrMore(NonTerminal('anything-but-newline')), '\\n'),
        Sequence('/*', ZeroOrMore(NonTerminal('anything-but-*/')), '*/'))))


export('Name.svg', Diagram(
    Choice(0,
        Sequence(
            Choice(1, '_', NonTerminal('alpha')),
            ZeroOrMore(Choice(1, '_', NonTerminal('alpha'), NonTerminal('numeric'), '$'))),
        Sequence(
            '[',
            OneOrMore(Choice(0, NonTerminal('anything-but-]'), ']]')),
            ']'),
        Sequence(
            '`',
            OneOrMore(Choice(0, NonTerminal('anything-but-`'), '``')),
            '`'),
        Sequence(
            '"',
            OneOrMore(Choice(0, NonTerminal('anything-but-"'), '""')),
            '"'))))

export('ObjectName.svg', Diagram(
    Optional(Sequence(name(), '.'), 'skip'), name()))

export('ColumnName.svg', Diagram(
    Optional(Sequence(object_name(), '.'), 'skip'), name()))

export('ColumnConstraint.svg', Diagram(
    Optional(Sequence('CONSTRAINT', name()), 'skip'),
    Choice(3,
        'NULL',
        'UNIQUE',
        Sequence('DEFAULT', expr()),
        Sequence('COLLATE', name()),
        primary_key_clause(),
        foreign_key_clause())))

export('ColumnDef.svg', Diagram(
    name(),
    type_name(),
    ZeroOrMore(column_constraint())))

export('TableConstraint.svg', Diagram(
    Stack(
        Optional(Sequence('CONSTRAINT', name()), 'skip'),
        Choice(0,
            Sequence('FOREIGN', 'KEY', '(', ZeroOrMore(name(), ','), ')', foreign_key_clause()),
            Sequence('CHECK', '(', expr(), ')'),
            Sequence(
                Choice(0, 'UNIQUE', Sequence('PRIMARY', 'KEY')),
                '(',
                OneOrMore(Sequence(name(), order_direction()), ','),
                ')')))))

export('CreateTable.svg', Diagram(Sequence(
    Stack(
        Sequence(
            'CREATE',
            Optional(Choice(0, 'TEMP', 'TEMPORARY'), 'skip'),
            'TABLE',
            object_name()),
        Choice(0,
            Sequence('AS', select_stmt()),
            Sequence(
                '(',
                ZeroOrMore(
                    Choice(0,
                        column_def(),
                        NonTerminal('table-constraint')),
                    ','),
                ')'))))))

export('AlterTable.svg', Diagram(Sequence(
    'ALTER',
    'TABLE',
    object_name(),
    Choice(0,
        Sequence(
            'ADD',
            'COLUMN',
            column_def()),
        Sequence(
            'RENAME',
            'TO',
            name())))))

export('CreateView.svg', Diagram(
    Stack(
        Sequence(
            'CREATE',
            Optional(Choice(0, 'TEMP', 'TEMPORARY'), 'skip'),
            'VIEW',
            object_name()),
        Sequence(Optional(Sequence('(', ZeroOrMore(name(), ','), ')')), 'AS', select_stmt()))))

export('TypeName.svg', Diagram(
    Choice(0,
        Sequence(
            Choice(0, 'STRING', 'BINARY'),
            Optional(Sequence('(', NonTerminal('max-length'), ')'))),
        'INT8',
        'INT16',
        Choice(0, 'INT32', 'INT'),
        'INT64',
        'FLOAT32',
        Choice(0, 'FLOAT64', 'FLOAT'),
        'DECIMAL',
        'BOOL',
        'GUID',
        'DATETIME',
        'DATETIMEOFFSET')))

export('Literal.svg', Diagram(
    Choice(0,
        'NULL',
        Choice(0, 'TRUE', 'FALSE'),
        Choice(0,
            Sequence(
                ZeroOrMore(NonTerminal('decimal-digit')),
                Optional(
                    Sequence('.', OneOrMore(NonTerminal('decimal-digit'))), 'skip'),
                Optional(
                    Sequence('e', Choice(0, Skip(), '+', '-'), OneOrMore(NonTerminal('decimal-digit'))))),
            Sequence('0x', OneOrMore(NonTerminal('hex-digit')))),
        Sequence("'", ZeroOrMore(Choice(0, NonTerminal('any-char-but-quote'), "''")), "'"),
        Sequence("x'", ZeroOrMore(NonTerminal('hex-digit-pair')), "'"),
        Sequence(
            NonTerminal('yyyy-MM-dd'),
            Optional(
                Sequence(
                    NonTerminal('THH:mm:ss'),
                    Optional(Sequence('.', NonTerminal('fff'))),
                    Optional(Sequence(Choice(0, '+', '-'), NonTerminal('HH:mm')))))))))

export('Expr.svg', Diagram(
    Choice(6,
        literal(),
        bind_parameter(),
        column_name(),
        Sequence(
            '(',
            Choice(0, expr(), select_stmt()),
            ')'),
        Sequence(
            'CAST',
            '(',
            expr(),
            'AS',
            type_name(),
            ')'),
        Sequence(
            Choice(0, 'NOT', '~', '-', '+'),
            expr()),
        Sequence(expr(),
            Choice(1,
                Sequence('COLLATE', name()),
                Sequence(NonTerminal('binary-operator'), expr()),
                Sequence(
                    Optional('NOT', 'skip'),
                    'LIKE',
                    expr(),
                    Optional(Sequence('ESCAPE', expr()), 'skip')),
                Sequence(
                    Optional('NOT', 'skip'),
                    'BETWEEN',
                    expr(),
                    'AND',
                    expr()),
                Sequence(
                    Optional('NOT', 'skip'),
                    'IN',
                    Choice(1,
                        object_name(),
                        Sequence(
                            '(',
                            Choice(0,
                                ZeroOrMore(expr(), ','),
                                select_stmt()),
                            ')'),
                        bind_parameter())))),
        Sequence(
            NonTerminal('function-name', language('Name', 'name')),
            '(',
            Choice(0,
                Sequence(Optional('DISTINCT', 'skip'), ZeroOrMore(expr(), ',')),
                '*'),
            ')'),
        Stack(
            Sequence(
                'CASE',
                Optional(expr(), 'skip'),
                OneOrMore(
                    Sequence('WHEN', expr(), 'THEN', expr()))),
            Sequence(
                Optional(Sequence('ELSE', expr()), 'skip'),
                'END')),
        Sequence(
            'EXISTS',
            '(',
            select_stmt(),
            ')'))))

export('SelectProperty.svg', Diagram(
    Choice(2,
        '*',
        Sequence(
            object_name(),
            '.',
            '*'),
        Sequence(
            expr(),
            Optional(Sequence(Optional('AS', 'skip'), name()))),
        Sequence(
            Choice(0, 'MANY', 'OPTIONAL', 'ONE'),
            name(),
            '(',
            OneOrMore(select_property(), ','),
            ')'))))

export('TableExpr.svg', Diagram(
    Choice(0,
        Sequence(
            Choice(0,
                object_name(),
                Sequence('(', select_stmt(), ')')),
            Optional(Sequence(
                Optional('AS', 'skip'),
                name()))),
        Sequence(
            table_expr(),
            Choice(0,
                Sequence(
                    Choice(0,
                        Skip(),
                        Sequence('LEFT', Optional('OUTER')),
                        'INNER'),
                    'JOIN'),
                ','),
            table_expr(),
            Optional(
                Sequence('ON', expr()))))))

export('SelectCore.svg', Diagram(
    Stack(
        Sequence(
            'SELECT',
            Optional('DISTINCT', 'skip'),
            OneOrMore(select_property())),
        Optional(Sequence('FROM', table_expr()), 'skip'),
        Optional(Sequence('WHERE', expr()), 'skip'),
        Optional(
            Sequence(
                'GROUP',
                'BY',
                OneOrMore(expr(), ','),
                Optional(Sequence('HAVING', expr()))), 'skip'))))

export('CompoundExpr.svg', Diagram(
    Choice(0,
        select_core(),
        Sequence(
            'VALUES',
            OneOrMore(
                Sequence('(', OneOrMore(expr(), ','), ')'),
                ',')),
        Sequence(
            compound_expr(),
            Choice(1,
                'INTERSECT',
                Sequence('UNION', Optional('ALL')),
                'EXCEPT'),
            compound_expr()))))

def order_by():
    return Sequence(
        'ORDER',
        'BY',
        OneOrMore(Sequence(expr(), order_direction()), ','))

def limit():
    return Sequence(
        'LIMIT',
        expr(),
        Optional(
            Sequence(Choice(0, 'OFFSET', ','), expr())))

export('SelectStmt.svg', Diagram(
    Stack(
        compound_expr(),
        Optional(order_by(), 'skip'),
        Optional(limit(), 'skip'))))

export('InsertStmt.svg', Diagram(
    'INSERT', # todo: OR IGNORE and pals?
    'INTO',
    object_name(),
    '(',
    OneOrMore(name(), ','),
    ')',
    select_stmt()))

export('UpdateStmt.svg', Diagram(
    Stack(
        Sequence(
            'UPDATE',
            object_name(),
            'SET',
            OneOrMore(
                Sequence(name(), '=', expr()),
                ',')),
        Optional(Sequence('WHERE', expr()), 'skip'),
        Optional(order_by(), 'skip'),
        Optional(limit(), 'skip'))))

export('DeleteStmt.svg', Diagram(
    Stack(
        Sequence(
            'DELETE',
            'FROM',
            object_name()),
        Optional(Sequence('WHERE', expr()), 'skip'),
        Optional(order_by(), 'skip'),
        Optional(limit(), 'skip'))))
