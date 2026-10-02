// Simple canvas Tetris. No wall kicks, no hold piece - just the classic game. Each piece's 4
// rotation states are pre-defined (the standard simple-Tetris approach) rather than computed on
// the fly, since rotating arbitrary offsets around a float center and rounding the result tends to
// collapse cells onto each other or drift the piece out of its box - checked that the hard way.
//
// The live score/lines/level/time HUD is updated directly in the DOM (no Blazor round-trip per
// tick) - Blazor only hears from this file once, when the game ends (OnGameOver).
//
// On-screen buttons (for mobile, no arrow keys) call the same tetrisMove/tetrisRotateBtn/
// tetrisHardDropBtn/tetrisSoftDropBtn wrappers the keyboard handler uses internally.
(function () {
    const COLS = 10, ROWS = 20, CELL = 24;
    const LEVEL_INTERVAL_MS = 60 * 1000;   // a new level (and faster drop) every 1 minute played
    const SPEEDUP_STEP_MS = 80;
    const MIN_DROP_MS = 120;

    // Points for clearing 1/2/3/4 lines in a single piece drop, before the level coefficient.
    const LINE_POINTS = [0, 100, 250, 400, 600];

    // Level 1 = ×1, level 2 = ×1.5, level 3 = ×2, ... (+0.5 per level).
    function levelCoefficient(level) {
        return 1 + (level - 1) * 0.5;
    }

    const PIECES = {
        I: { color: '#4dd0e1', rotations: [
            [[0,1],[1,1],[2,1],[3,1]],
            [[2,0],[2,1],[2,2],[2,3]],
            [[0,2],[1,2],[2,2],[3,2]],
            [[1,0],[1,1],[1,2],[1,3]],
        ]},
        O: { color: '#ffd54f', rotations: Array(4).fill([[1,0],[2,0],[1,1],[2,1]]) },
        T: { color: '#ba68c8', rotations: [
            [[1,0],[0,1],[1,1],[2,1]],
            [[1,0],[1,1],[2,1],[1,2]],
            [[0,1],[1,1],[2,1],[1,2]],
            [[1,0],[0,1],[1,1],[1,2]],
        ]},
        S: { color: '#81c784', rotations: [
            [[1,0],[2,0],[0,1],[1,1]],
            [[1,0],[1,1],[2,1],[2,2]],
            [[1,0],[2,0],[0,1],[1,1]],
            [[1,0],[1,1],[2,1],[2,2]],
        ]},
        Z: { color: '#e57373', rotations: [
            [[0,0],[1,0],[1,1],[2,1]],
            [[2,0],[1,1],[2,1],[1,2]],
            [[0,0],[1,0],[1,1],[2,1]],
            [[2,0],[1,1],[2,1],[1,2]],
        ]},
        J: { color: '#64b5f6', rotations: [
            [[0,0],[0,1],[1,1],[2,1]],
            [[1,0],[2,0],[1,1],[1,2]],
            [[0,1],[1,1],[2,1],[2,2]],
            [[1,0],[1,1],[0,2],[1,2]],
        ]},
        L: { color: '#ffb74d', rotations: [
            [[2,0],[0,1],[1,1],[2,1]],
            [[1,0],[1,1],[1,2],[2,2]],
            [[0,1],[1,1],[2,1],[0,2]],
            [[0,0],[1,0],[1,1],[1,2]],
        ]},
    };
    const KEYS = Object.keys(PIECES);

    let ctx, dotNetRef, board, cur, score, lines, level, dropTimer, dropMs, running, keyHandler;
    let startedAt, elapsedTimer, levelTimer;

    function emptyBoard() {
        return Array.from({ length: ROWS }, () => Array(COLS).fill(null));
    }

    function cellsFor(piece) {
        return PIECES[piece.key].rotations[piece.rot].map(([dx, dy]) => ({ x: piece.ox + dx, y: piece.oy + dy }));
    }

    function newPiece() {
        const key = KEYS[Math.floor(Math.random() * KEYS.length)];
        return { key, rot: 0, ox: 3, oy: -1, color: PIECES[key].color };
    }

    function collides(piece) {
        return cellsFor(piece).some(({ x, y }) =>
            x < 0 || x >= COLS || y >= ROWS || (y >= 0 && board[y][x]));
    }

    function elapsedSeconds() {
        return Math.floor((Date.now() - startedAt) / 1000);
    }

    function formatTime(totalSeconds) {
        const m = Math.floor(totalSeconds / 60);
        const s = totalSeconds % 60;
        return `${m}:${s.toString().padStart(2, '0')}`;
    }

    function setText(id, text) {
        const el = document.getElementById(id);
        if (el) el.textContent = text;
    }

    function updateHud() {
        setText('tetris-score', score);
        setText('tetris-lines', lines);
        setText('tetris-level', level);
        setText('tetris-time', formatTime(elapsedSeconds()));
    }

    function lockPiece() {
        cellsFor(cur).forEach(({ x, y }) => { if (y >= 0) board[y][x] = cur.color; });

        let cleared = 0;
        for (let y = ROWS - 1; y >= 0; y--) {
            if (board[y].every(c => c)) {
                board.splice(y, 1);
                board.unshift(Array(COLS).fill(null));
                cleared++;
                y++;
            }
        }

        if (cleared > 0) {
            score += Math.round((LINE_POINTS[cleared] || 0) * levelCoefficient(level));
            lines += cleared;
            updateHud();
        }

        cur = newPiece();
        if (collides(cur)) {
            gameOver();
            return;
        }
        draw();
    }

    function move(dx, dy) {
        const moved = { ...cur, ox: cur.ox + dx, oy: cur.oy + dy };
        if (collides(moved)) {
            if (dy > 0) lockPiece();
            return false;
        }
        cur = moved;
        draw();
        return true;
    }

    function doRotate() {
        const rotated = { ...cur, rot: (cur.rot + 1) % 4 };
        if (!collides(rotated)) { cur = rotated; draw(); }
    }

    function hardDrop() {
        while (move(0, 1)) { /* keep dropping */ }
    }

    function draw() {
        ctx.clearRect(0, 0, COLS * CELL, ROWS * CELL);

        ctx.fillStyle = '#111';
        ctx.fillRect(0, 0, COLS * CELL, ROWS * CELL);

        for (let y = 0; y < ROWS; y++)
            for (let x = 0; x < COLS; x++)
                if (board[y][x]) drawCell(x, y, board[y][x]);

        cellsFor(cur).forEach(({ x, y }) => { if (y >= 0) drawCell(x, y, cur.color); });

        ctx.strokeStyle = 'rgba(255,255,255,.06)';
        for (let x = 0; x <= COLS; x++) { ctx.beginPath(); ctx.moveTo(x * CELL, 0); ctx.lineTo(x * CELL, ROWS * CELL); ctx.stroke(); }
        for (let y = 0; y <= ROWS; y++) { ctx.beginPath(); ctx.moveTo(0, y * CELL); ctx.lineTo(COLS * CELL, y * CELL); ctx.stroke(); }
    }

    function drawCell(x, y, color) {
        ctx.fillStyle = color;
        ctx.fillRect(x * CELL + 1, y * CELL + 1, CELL - 2, CELL - 2);
    }

    function tick() {
        if (!running) return;
        move(0, 1);
    }

    function gameOver() {
        running = false;
        clearInterval(dropTimer);
        clearInterval(elapsedTimer);
        clearInterval(levelTimer);
        updateHud();
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnGameOver', score, lines, elapsedSeconds(), level);
    }

    function scheduleDrop() {
        clearInterval(dropTimer);
        dropTimer = setInterval(tick, dropMs);
    }

    window.tetrisInit = function (canvasId, ref) {
        const canvas = document.getElementById(canvasId);
        ctx = canvas.getContext('2d');
        dotNetRef = ref;

        // "Play again" calls tetrisInit again on the same page - remove any listener from a
        // previous game first, or every keypress ends up firing once per listener stacked up.
        if (keyHandler) document.removeEventListener('keydown', keyHandler);

        keyHandler = function (e) {
            if (!running) return;
            switch (e.key) {
                case 'ArrowLeft':  move(-1, 0); e.preventDefault(); break;
                case 'ArrowRight': move(1, 0);  e.preventDefault(); break;
                case 'ArrowDown':  move(0, 1);  e.preventDefault(); break;
                case 'ArrowUp':    doRotate();  e.preventDefault(); break;
                case ' ':          hardDrop();  e.preventDefault(); break;
            }
        };
        document.addEventListener('keydown', keyHandler);

        window.tetrisStart();
    };

    window.tetrisStart = function () {
        board = emptyBoard();
        score = 0; lines = 0; level = 1; dropMs = 800;
        cur = newPiece();
        running = true;
        startedAt = Date.now();

        draw();
        updateHud();
        scheduleDrop();

        clearInterval(elapsedTimer);
        elapsedTimer = setInterval(() => setText('tetris-time', formatTime(elapsedSeconds())), 1000);

        clearInterval(levelTimer);
        levelTimer = setInterval(() => {
            level += 1;
            dropMs = Math.max(MIN_DROP_MS, dropMs - SPEEDUP_STEP_MS);
            scheduleDrop();
            updateHud();
        }, LEVEL_INTERVAL_MS);
    };

    window.tetrisDestroy = function () {
        running = false;
        clearInterval(dropTimer);
        clearInterval(elapsedTimer);
        clearInterval(levelTimer);
        if (keyHandler) document.removeEventListener('keydown', keyHandler);
    };

    // On-screen button wrappers (mobile, no keyboard) - same guard as the keyboard handler.
    window.tetrisMove = function (dx, dy) { if (running) move(dx, dy); };
    window.tetrisRotateBtn = function () { if (running) doRotate(); };
    window.tetrisHardDropBtn = function () { if (running) hardDrop(); };
})();
