angular.module('stats').controller('UsersController', ["$scope", "$http", function ($scope, $http) {
    $scope.isAdmin = !!(window.UserVars && window.UserVars.IsAdmin);
    $scope.items = [];

    if (!$scope.isAdmin) {
        return;
    }

    $scope.load = function () {
        return $http.get('/api/Users').then(function (r) {
            $scope.items = r.data || [];
            return $scope.items;
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось загрузить пользователей.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        });
    };

    $scope.save = function (u) {
        return $http.put('/api/Users', u).then(function () {
            if (window.toastr && toastr.success) toastr.success('Сохранено');
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось сохранить.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        });
    };

    $scope.remove = function (u) {
        if (!u || !u.UserName) return;
        if (!confirm('Удалить пользователя ' + u.UserName + '?')) return;
        return $http.delete('/api/Users?username=' + encodeURIComponent(u.UserName)).then(function () {
            $scope.load();
        }, function (err) {
            var msg = (err && err.data && (err.data.Message || err.data.message)) || 'Не удалось удалить.';
            if (window.toastr && toastr.error) toastr.error(msg); else alert(msg);
        });
    };

    $scope.load();
}]);
