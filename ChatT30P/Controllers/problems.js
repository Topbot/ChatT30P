angular.module('stats').controller('ProblemsController', ["$scope", "$http", function ($scope, $http) {
    $scope.items = [];
    $scope.searchText = "";
    $scope.selectedMap = {};
    $scope._loading = false;
    $scope._saving = false;
    $scope._adding = false;
    $scope._savingNotify = false;
    $scope.newProblemTitle = "";
    $scope.newProblemDescription = "";
    $scope.notifyItems = [];
    $scope.notifyMap = {};

    $scope.problemFilter = function (item) {
        if (!$scope.searchText) return true;
        var text = ($scope.searchText || '').toLowerCase();
        var title = (item && item.Title ? item.Title : '').toLowerCase();
        var desc = (item && item.Description ? item.Description : '').toLowerCase();
        return title.indexOf(text) >= 0 || desc.indexOf(text) >= 0;
    };

    $scope.refresh = function () {
        if ($scope._loading) return;
        $scope._loading = true;
        $http.get('/api/Problems').then(function (r) {
            $scope.items = r.data || [];
            var map = {};
            var notifyMap = {};
            for (var i = 0; i < $scope.items.length; i++) {
                var item = $scope.items[i];
                if (item && item.IsSelected) {
                    map[item.ProblemId] = true;
                }
                if (item && item.Notify) {
                    notifyMap[item.ProblemId] = true;
                }
            }
            $scope.selectedMap = map;
            $scope.notifyMap = notifyMap;
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось загрузить проблемы.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            $scope._loading = false;
        });
    };

    $scope.remove = function (item) {
        if (!item || !item.ProblemId) return;
        if (!confirm('Удалить проблему "' + (item.Title || item.ProblemId) + '"?')) return;
        $http.delete('/api/Problems?problemId=' + encodeURIComponent(item.ProblemId)).then(function () {
            $scope.items = ($scope.items || []).filter(function (x) { return x.ProblemId !== item.ProblemId; });
            delete $scope.selectedMap[item.ProblemId];
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось удалить проблему.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        });
    };

    $scope.saveSelection = function () {
        if ($scope._saving) return;
        $scope._saving = true;
        var ids = [];
        angular.forEach($scope.selectedMap, function (value, key) {
            if (value) ids.push(parseInt(key));
        });
        $http.put('/api/Problems/Selection', ids).then(function () {
            if (window.toastr && toastr.success) toastr.success('Сохранено');
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось сохранить выбор.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            $scope._saving = false;
        });
    };

    $scope.showAddModal = function () {
        if (window.$) {
            $('#modal-add-problem').modal();
        }
    };

    $scope.showNotifyModal = function () {
        var selected = ($scope.items || []).filter(function (item) {
            return item && $scope.selectedMap[item.ProblemId];
        });
        if (!selected || selected.length === 0) {
            var msg = 'Вначале выберете проблемы для отслеживания.';
            if (window.toastr && toastr.warning) toastr.warning(msg); else alert(msg);
            return;
        }
        $scope.notifyItems = selected;
        var map = {};
        for (var i = 0; i < selected.length; i++) {
            var it = selected[i];
            if (it && it.Notify) {
                map[it.ProblemId] = true;
            } else if ($scope.notifyMap[it.ProblemId]) {
                map[it.ProblemId] = true;
            }
        }
        $scope.notifyMap = map;
        if (window.$) {
            $('#modal-notify-problem').modal();
        }
    };

    $scope.addProblem = function () {
        if ($scope._adding) return;
        var title = ($scope.newProblemTitle || '').trim();
        if (!title) return;
        $scope._adding = true;
        var payload = {
            Title: title,
            Description: ($scope.newProblemDescription || '').trim()
        };
        $http.post('/api/Problems', payload).then(function (r) {
            var item = r.data;
            if (item) {
                $scope.items.push(item);
                if (item.ProblemId) {
                    $scope.selectedMap[item.ProblemId] = true;
                }
            }
            $scope.newProblemTitle = '';
            $scope.newProblemDescription = '';
            if (window.$) {
                $('#modal-add-problem').modal('hide');
            }
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось добавить проблему.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            $scope._adding = false;
        });
    };

    $scope.saveNotify = function () {
        if ($scope._savingNotify) return;
        var ids = [];
        angular.forEach($scope.notifyMap, function (value, key) {
            if (value) ids.push(parseInt(key));
        });
        $scope._savingNotify = true;
        $http.put('/api/Problems/Notify', { ProblemIds: ids }).then(function () {
            for (var i = 0; i < ($scope.items || []).length; i++) {
                var item = $scope.items[i];
                if (!item) continue;
                item.Notify = !!$scope.notifyMap[item.ProblemId];
            }
            if (window.toastr && toastr.success) toastr.success('Сохранено');
            if (window.$) {
                $('#modal-notify-problem').modal('hide');
            }
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось сохранить уведомления.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        }).finally(function () {
            $scope._savingNotify = false;
        });
    };

    $scope.refresh();
}]);
